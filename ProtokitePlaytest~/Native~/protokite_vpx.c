/*
 * protokite_vpx: a flat C surface over libvpx for the Protokite Playtest package, so C# never lays out libvpx's own structs.
 * Built by build-protokite-vpx.sh into ../Runtime/Plugins/x86_64/protokite_vpx.dll (Win64, static CRT, KERNEL32 only).
 * Change PK_WRAPPER_VERSION with any change to these functions: the package refuses a DLL whose version differs.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "vpx_config.h"
#include "vpx/vpx_encoder.h"
#include "vpx/vpx_decoder.h"
#include "vpx/vp8cx.h"
#include "vpx/vp8dx.h"

#define PK_API __declspec(dllexport)
#define PK_WRAPPER_VERSION 1
#define PK_ERROR_SIZE 256

typedef struct {
    vpx_codec_ctx_t codec;
    vpx_image_t image;
    vpx_codec_iter_t iter;
    int width, height;
    char last_error[PK_ERROR_SIZE];
} pk_encoder;

typedef struct {
    vpx_codec_ctx_t codec;
    char last_error[PK_ERROR_SIZE];
} pk_decoder;

static void pk_copy_error(char* dst, vpx_codec_ctx_t* codec, const char* fallback) {
    const char* detail = codec ? vpx_codec_error_detail(codec) : NULL;
    const char* text = codec ? vpx_codec_error(codec) : fallback;
    snprintf(dst, PK_ERROR_SIZE, "%s%s%s", text ? text : fallback, detail ? ": " : "", detail ? detail : "");
}

static void pk_set_error(char* dst, int size, const char* text) {
    if (dst && size > 0) snprintf(dst, (size_t)size, "%s", text);
}

/* The size of one tightly packed I420 frame: a full-size brightness plane and two half-size colour planes. */
static int64_t pk_i420_size(int width, int height) {
    int64_t cw = (width + 1) / 2, ch = (height + 1) / 2;
    return (int64_t)width * height + 2 * cw * ch;
}

static vpx_codec_iface_t* pk_encoder_iface(int codec) {
#if CONFIG_VP8_ENCODER
    if (codec == 8) return vpx_codec_vp8_cx();
#endif
#if CONFIG_VP9_ENCODER
    if (codec == 9) return vpx_codec_vp9_cx();
#endif
    return NULL;
}

static vpx_codec_iface_t* pk_decoder_iface(int codec) {
#if CONFIG_VP8_DECODER
    if (codec == 8) return vpx_codec_vp8_dx();
#endif
#if CONFIG_VP9_DECODER
    if (codec == 9) return vpx_codec_vp9_dx();
#endif
    return NULL;
}

PK_API int pk_vpx_wrapper_version(void) { return PK_WRAPPER_VERSION; }

PK_API const char* pk_vpx_version(void) { return vpx_codec_version_str(); }

/* 1 when this build can encode the codec (8 for VP8, 9 for VP9), else 0. */
PK_API int pk_vpx_has_codec(int codec) { return pk_encoder_iface(codec) != NULL; }

/*
 * One pass, no frames held back, a steady bitrate. Timestamps are milliseconds. speed is libvpx's cpu-used (VP8 -16 to 16,
 * VP9 -9 to 9; higher is faster). Returns NULL and writes why into error when it cannot start.
 */
PK_API pk_encoder* pk_vpx_encoder_create(int codec, int width, int height, int fps, int bitrate_kbps, int threads,
                                         int keyframe_interval, int speed, char* error, int error_size) {
    vpx_codec_iface_t* iface = pk_encoder_iface(codec);
    vpx_codec_enc_cfg_t cfg;
    pk_encoder* enc;
    int partitions = 0;
    if (!iface) { pk_set_error(error, error_size, "this encoder build does not include the codec asked for"); return NULL; }
    if (width < 2 || height < 2 || (width & 1) || (height & 1)) { pk_set_error(error, error_size, "width and height must be even and at least 2"); return NULL; }
    if (fps < 1 || bitrate_kbps < 1 || threads < 1 || keyframe_interval < 1) { pk_set_error(error, error_size, "frames per second, bitrate, threads and keyframe interval must be at least 1"); return NULL; }
    /* libvpx takes any speed and quietly clamps it (measured, 1.17.0), so the range is checked here. */
    if ((codec == 8 && (speed < -16 || speed > 16)) || (codec == 9 && (speed < -9 || speed > 9))) {
        pk_set_error(error, error_size, codec == 8 ? "the speed must be -16 to 16 for VP8" : "the speed must be -9 to 9 for VP9");
        return NULL;
    }
    if (vpx_codec_enc_config_default(iface, &cfg, 0) != VPX_CODEC_OK) { pk_set_error(error, error_size, "libvpx has no default settings for the codec"); return NULL; }
    enc = (pk_encoder*)calloc(1, sizeof(pk_encoder));
    if (!enc) { pk_set_error(error, error_size, "out of memory"); return NULL; }
    cfg.g_w = (unsigned)width;
    cfg.g_h = (unsigned)height;
    cfg.g_timebase.num = 1;
    cfg.g_timebase.den = 1000;
    cfg.rc_target_bitrate = (unsigned)bitrate_kbps;
    cfg.rc_end_usage = VPX_CBR;
    cfg.g_threads = (unsigned)threads;
    cfg.g_lag_in_frames = 0;
    cfg.g_error_resilient = 0;
    cfg.g_pass = VPX_RC_ONE_PASS;
    cfg.kf_mode = VPX_KF_AUTO;
    cfg.kf_min_dist = 0;
    cfg.kf_max_dist = (unsigned)keyframe_interval;
    if (vpx_codec_enc_init(&enc->codec, iface, &cfg, 0) != VPX_CODEC_OK) {
        pk_copy_error(enc->last_error, &enc->codec, "the encoder refused its settings");
        pk_set_error(error, error_size, enc->last_error);
        free(enc);
        return NULL;
    }
    if (vpx_codec_control(&enc->codec, VP8E_SET_CPUUSED, speed) != VPX_CODEC_OK) {
        pk_set_error(error, error_size, "the encoder refused the speed setting");
        vpx_codec_destroy(&enc->codec);
        free(enc);
        return NULL;
    }
    if (codec == 8) {
        /* VP8 spreads work over threads through token partitions: 2^partitions, one per thread. */
        while ((1 << (partitions + 1)) <= threads && partitions < 3) partitions++;
        vpx_codec_control(&enc->codec, VP8E_SET_TOKEN_PARTITIONS, partitions);
    } else {
        vpx_codec_control(&enc->codec, VP9E_SET_ROW_MT, threads > 1 ? 1 : 0);
        vpx_codec_control(&enc->codec, VP9E_SET_TILE_COLUMNS, threads >= 4 ? 2 : (threads >= 2 ? 1 : 0));
    }
    if (!vpx_img_alloc(&enc->image, VPX_IMG_FMT_I420, (unsigned)width, (unsigned)height, 32)) {
        pk_set_error(error, error_size, "out of memory for the frame");
        vpx_codec_destroy(&enc->codec);
        free(enc);
        return NULL;
    }
    enc->width = width;
    enc->height = height;
    return enc;
}

/*
 * Encodes one tightly packed I420 frame shown at pts_ms for duration_ms. Refuses a buffer of any other length, so libvpx
 * never reads past it. Returns 0 on success; packets are then read with pk_vpx_next_packet.
 */
PK_API int pk_vpx_encode_i420(pk_encoder* enc, const uint8_t* i420, int64_t i420_length, int64_t pts_ms, int64_t duration_ms, int force_keyframe) {
    int y, cw, ch;
    const uint8_t *src_y, *src_u, *src_v;
    int64_t expected;
    if (!enc) return -1;
    expected = pk_i420_size(enc->width, enc->height);
    if (!i420 || i420_length != expected) {
        snprintf(enc->last_error, PK_ERROR_SIZE, "the frame is %lld bytes, and a %dx%d I420 frame is %lld",
                 (long long)i420_length, enc->width, enc->height, (long long)expected);
        return -1;
    }
    if (duration_ms < 1) { snprintf(enc->last_error, PK_ERROR_SIZE, "a frame lasts at least 1 ms"); return -1; }
    cw = (enc->width + 1) / 2;
    ch = (enc->height + 1) / 2;
    src_y = i420;
    src_u = src_y + (int64_t)enc->width * enc->height;
    src_v = src_u + (int64_t)cw * ch;
    for (y = 0; y < enc->height; y++) memcpy(enc->image.planes[0] + y * enc->image.stride[0], src_y + (int64_t)y * enc->width, (size_t)enc->width);
    for (y = 0; y < ch; y++) memcpy(enc->image.planes[1] + y * enc->image.stride[1], src_u + (int64_t)y * cw, (size_t)cw);
    for (y = 0; y < ch; y++) memcpy(enc->image.planes[2] + y * enc->image.stride[2], src_v + (int64_t)y * cw, (size_t)cw);
    enc->iter = NULL;
    if (vpx_codec_encode(&enc->codec, &enc->image, pts_ms, (unsigned long)duration_ms, force_keyframe ? VPX_EFLAG_FORCE_KF : 0, VPX_DL_REALTIME) != VPX_CODEC_OK) {
        pk_copy_error(enc->last_error, &enc->codec, "encode failed");
        return -1;
    }
    return 0;
}

/* Hands over anything the encoder still holds; packets are then read with pk_vpx_next_packet. Returns 0 on success. */
PK_API int pk_vpx_flush(pk_encoder* enc) {
    if (!enc) return -1;
    enc->iter = NULL;
    if (vpx_codec_encode(&enc->codec, NULL, -1, 1, 0, VPX_DL_REALTIME) != VPX_CODEC_OK) {
        pk_copy_error(enc->last_error, &enc->codec, "flush failed");
        return -1;
    }
    return 0;
}

/* Returns 1 and fills the out values while compressed frames remain from the last encode or flush, else 0. */
PK_API int pk_vpx_next_packet(pk_encoder* enc, const uint8_t** data, int* size, int64_t* pts_ms, int* is_keyframe) {
    const vpx_codec_cx_pkt_t* pkt;
    if (!enc) return 0;
    while ((pkt = vpx_codec_get_cx_data(&enc->codec, &enc->iter)) != NULL) {
        if (pkt->kind != VPX_CODEC_CX_FRAME_PKT) continue;
        *data = (const uint8_t*)pkt->data.frame.buf;
        *size = (int)pkt->data.frame.sz;
        *pts_ms = pkt->data.frame.pts;
        *is_keyframe = (pkt->data.frame.flags & VPX_FRAME_IS_KEY) ? 1 : 0;
        return 1;
    }
    return 0;
}

PK_API const char* pk_vpx_encoder_error(pk_encoder* enc) { return enc ? enc->last_error : "no encoder"; }

PK_API void pk_vpx_encoder_destroy(pk_encoder* enc) {
    if (!enc) return;
    vpx_img_free(&enc->image);
    vpx_codec_destroy(&enc->codec);
    free(enc);
}

/* A decoder for either codec, so a recording can be checked by decoding it. NULL when the build lacks the codec. */
PK_API pk_decoder* pk_vpx_decoder_create(int codec) {
    vpx_codec_iface_t* iface = pk_decoder_iface(codec);
    pk_decoder* dec;
    if (!iface) return NULL;
    dec = (pk_decoder*)calloc(1, sizeof(pk_decoder));
    if (!dec) return NULL;
    if (vpx_codec_dec_init(&dec->codec, iface, NULL, 0) != VPX_CODEC_OK) { free(dec); return NULL; }
    return dec;
}

/* Decodes one compressed frame into a tightly packed I420 buffer of exactly out_length bytes. Returns 0 on success. */
PK_API int pk_vpx_decode_to_i420(pk_decoder* dec, const uint8_t* data, int size, uint8_t* out_i420, int64_t out_length, int width, int height) {
    vpx_codec_iter_t iter = NULL;
    vpx_image_t* img;
    int y, cw = (width + 1) / 2, ch = (height + 1) / 2;
    if (!dec) return -1;
    if (!out_i420 || out_length != pk_i420_size(width, height)) { snprintf(dec->last_error, PK_ERROR_SIZE, "the output buffer is the wrong size"); return -4; }
    if (vpx_codec_decode(&dec->codec, data, (unsigned)size, NULL, 0) != VPX_CODEC_OK) {
        pk_copy_error(dec->last_error, &dec->codec, "decode failed");
        return -1;
    }
    img = vpx_codec_get_frame(&dec->codec, &iter);
    if (!img) { snprintf(dec->last_error, PK_ERROR_SIZE, "no frame decoded"); return -2; }
    if ((int)img->d_w != width || (int)img->d_h != height) { snprintf(dec->last_error, PK_ERROR_SIZE, "decoded %ux%u", img->d_w, img->d_h); return -3; }
    for (y = 0; y < height; y++) memcpy(out_i420 + (int64_t)y * width, img->planes[0] + y * img->stride[0], (size_t)width);
    for (y = 0; y < ch; y++) memcpy(out_i420 + (int64_t)width * height + (int64_t)y * cw, img->planes[1] + y * img->stride[1], (size_t)cw);
    for (y = 0; y < ch; y++) memcpy(out_i420 + (int64_t)width * height + (int64_t)cw * ch + (int64_t)y * cw, img->planes[2] + y * img->stride[2], (size_t)cw);
    return 0;
}

PK_API const char* pk_vpx_decoder_error(pk_decoder* dec) { return dec ? dec->last_error : "no decoder"; }

PK_API void pk_vpx_decoder_destroy(pk_decoder* dec) {
    if (!dec) return;
    vpx_codec_destroy(&dec->codec);
    free(dec);
}
