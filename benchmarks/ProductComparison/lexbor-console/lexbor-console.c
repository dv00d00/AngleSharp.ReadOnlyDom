/*
 * Lexbor comparison lane: the standalone HTML tokenizer from lexbor (C), the usual
 * "fastest spec-compliant HTML parser" reference point, driven through the same RESULT
 * protocol and chunked push shape as the lol-html console. Extracts a[href] via the
 * token_done callback; entities in attribute values are decoded by the tokenizer, so
 * value checksums are comparable with the lol-html and AngleSharp lanes.
 *
 * Build with scripts/build-lexbor-console.ps1 (clones lexbor, generates the single-file
 * amalgamation, compiles with cl /O2 /GL /arch:AVX2).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
/* windows.h defines `interface` (COM) as a macro; lexbor uses it as an identifier. */
#undef interface
static double
now_ms(void)
{
    static LARGE_INTEGER frequency;
    static int initialized = 0;
    LARGE_INTEGER counter;
    if (!initialized) {
        QueryPerformanceFrequency(&frequency);
        initialized = 1;
    }
    QueryPerformanceCounter(&counter);
    return (double) counter.QuadPart * 1000.0 / (double) frequency.QuadPart;
}
#else
#include <time.h>
static double
now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double) ts.tv_sec * 1000.0 + (double) ts.tv_nsec / 1e6;
}
#endif

#include "lexbor_single.h"

typedef struct {
    size_t count;
    uint64_t checksum;
    bool collect;
} extract_ctx;

static lxb_html_token_t *
token_callback(lxb_html_tokenizer_t *tkz, lxb_html_token_t *token, void *raw)
{
    (void) tkz;
    extract_ctx *ctx = raw;
    if (token->tag_id != LXB_TAG_A || (token->type & LXB_HTML_TOKEN_TYPE_CLOSE)) {
        return token;
    }
    for (lxb_html_token_attr_t *attr = token->attr_first; attr != NULL; attr = attr->next) {
        size_t length = 0;
        const lxb_char_t *name = lxb_html_token_attr_name(attr, &length);
        if (name != NULL && length == 4 && memcmp(name, "href", 4) == 0) {
            ctx->count++;
            if (ctx->collect && attr->value != NULL) {
                for (size_t i = 0; i < attr->value_size; i++) {
                    ctx->checksum = ctx->checksum * 31 + attr->value[i];
                }
            }
            break;
        }
    }
    return token;
}

static void
parse(const uint8_t *input, size_t length, size_t chunk_size, bool collect,
      size_t *count, int64_t *checksum)
{
    extract_ctx ctx = { 0, 17, collect };
    lxb_html_tokenizer_t *tkz = lxb_html_tokenizer_create();
    if (tkz == NULL || lxb_html_tokenizer_init(tkz) != LXB_STATUS_OK) {
        fprintf(stderr, "tokenizer init failed\n");
        exit(EXIT_FAILURE);
    }
    lxb_html_tokenizer_callback_token_done_set(tkz, token_callback, &ctx);
    if (lxb_html_tokenizer_begin(tkz) != LXB_STATUS_OK) {
        fprintf(stderr, "tokenizer begin failed\n");
        exit(EXIT_FAILURE);
    }
    for (size_t offset = 0; offset < length; offset += chunk_size) {
        size_t slice = length - offset < chunk_size ? length - offset : chunk_size;
        if (lxb_html_tokenizer_chunk(tkz, input + offset, slice) != LXB_STATUS_OK) {
            fprintf(stderr, "tokenizer chunk failed\n");
            exit(EXIT_FAILURE);
        }
    }
    if (lxb_html_tokenizer_end(tkz) != LXB_STATUS_OK) {
        fprintf(stderr, "tokenizer end failed\n");
        exit(EXIT_FAILURE);
    }
    lxb_html_tokenizer_destroy(tkz);
    *count = ctx.count;
    *checksum = (int64_t) ctx.checksum;
}

int
main(int argc, const char *argv[])
{
    const char *input_path = NULL;
    double seconds = 10.0;
    long warmup = 120;
    long copies = 1;
    size_t chunk_size = 4096;
    const char *workload = "extract";

    for (int i = 1; i + 1 < argc; i += 2) {
        const char *name = argv[i];
        const char *value = argv[i + 1];
        if (strcmp(name, "--input") == 0) input_path = value;
        else if (strcmp(name, "--seconds") == 0) seconds = atof(value);
        else if (strcmp(name, "--warmup") == 0) warmup = atol(value);
        else if (strcmp(name, "--copies") == 0) copies = atol(value);
        else if (strcmp(name, "--chunk-size") == 0) chunk_size = (size_t) atol(value);
        else if (strcmp(name, "--workload") == 0) workload = value;
        else if (strcmp(name, "--query") == 0 || strcmp(name, "--mode") == 0
                 || strcmp(name, "--unlimited") == 0) { /* harness compatibility */ }
        else {
            fprintf(stderr, "unknown option: %s\n", name);
            return EXIT_FAILURE;
        }
    }
    if (input_path == NULL || copies != 1
        || (strcmp(workload, "extract") != 0 && strcmp(workload, "match") != 0)) {
        fprintf(stderr, "usage: --input <file> [--seconds N] [--warmup N] "
                        "[--chunk-size N] [--workload match|extract]\n");
        return EXIT_FAILURE;
    }
    bool collect = strcmp(workload, "extract") == 0;

    FILE *file = fopen(input_path, "rb");
    if (file == NULL) {
        fprintf(stderr, "failed to read input\n");
        return EXIT_FAILURE;
    }
    fseek(file, 0, SEEK_END);
    long file_size = ftell(file);
    fseek(file, 0, SEEK_SET);
    uint8_t *input = malloc((size_t) file_size);
    if (input == NULL || fread(input, 1, (size_t) file_size, file) != (size_t) file_size) {
        fprintf(stderr, "failed to read input\n");
        return EXIT_FAILURE;
    }
    fclose(file);

    size_t count = 0;
    int64_t value_checksum = 0;
    for (long i = 0; i < warmup; i++) {
        parse(input, (size_t) file_size, chunk_size, collect, &count, &value_checksum);
    }

    double started = now_ms();
    uint64_t requests = 0;
    uint64_t checksum = 0;
    double elapsed;
    for (;;) {
        parse(input, (size_t) file_size, chunk_size, collect, &count, &value_checksum);
        checksum += (uint64_t) value_checksum;
        requests++;
        elapsed = now_ms() - started;
        if (elapsed >= seconds * 1000.0) {
            break;
        }
    }

    printf("RESULT service=lexbor workload=%s copies=%ld requests=%llu elapsed_ms=%.3f "
           "cpu_ms=nan checksum=%lld value_checksum=%lld urls=%zu bytes=%ld\n",
           workload, copies, (unsigned long long) requests, elapsed,
           (long long) (int64_t) checksum, (long long) value_checksum, count, file_size);
    return EXIT_SUCCESS;
}
