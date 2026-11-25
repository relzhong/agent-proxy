#define SECURITY_WIN32
#include "pb_schannel.h"
#include "pb_tls.h"
#include <security.h>
#include <schannel.h>
#include <stdlib.h>
#include <string.h>

#pragma comment(lib, "secur32.lib")

#ifndef SCH_USE_STRONG_CRYPTO
#define SCH_USE_STRONG_CRYPTO 0x00400000
#endif

struct PB_TLS_CLIENT {
    SOCKET sock;
    CredHandle cred;
    CtxtHandle ctx;
    SecPkgContext_StreamSizes sizes;
    BOOL has_cred;
    BOOL has_ctx;
    BOOL insecure;
    BOOL peer_closed;
    BOOL need_more_net;
    char sni_host[256];
    unsigned char *in_buf;
    size_t in_cap;
    size_t in_len;
    unsigned char *plain_buf;
    size_t plain_cap;
    size_t plain_off;
    size_t plain_len;
};

static int send_raw_all(SOCKET sock, const unsigned char *data, int len, DWORD *out_error)
{
    int total = 0;
    while (total < len) {
        int sent = send(sock, (const char *)(data + total), len - total, 0);
        if (sent <= 0) {
            if (out_error) *out_error = (sent == SOCKET_ERROR) ? (DWORD)WSAGetLastError() : ERROR_GRACEFUL_DISCONNECT;
            return -1;
        }
        total += sent;
    }
    return 0;
}

static int ensure_in_capacity(PB_TLS_CLIENT *tls, size_t needed)
{
    if (needed <= tls->in_cap) return 0;
    size_t next = tls->in_cap ? tls->in_cap : 32768;
    while (next < needed) {
        if (next > (64u * 1024u * 1024u)) return -1;
        next *= 2;
    }
    unsigned char *grown = (unsigned char *)realloc(tls->in_buf, next);
    if (!grown) return -1;
    tls->in_buf = grown;
    tls->in_cap = next;
    return 0;
}

static int append_plain(PB_TLS_CLIENT *tls, const unsigned char *data, size_t len)
{
    if (len == 0) return 0;
    if (tls->plain_off > 0 && tls->plain_off == tls->plain_len) {
        tls->plain_off = 0;
        tls->plain_len = 0;
    } else if (tls->plain_off > 0 && (tls->plain_len + len > tls->plain_cap)) {
        size_t avail = tls->plain_len - tls->plain_off;
        memmove(tls->plain_buf, tls->plain_buf + tls->plain_off, avail);
        tls->plain_off = 0;
        tls->plain_len = avail;
    }
    if (tls->plain_len + len > tls->plain_cap) {
        size_t next = tls->plain_cap ? tls->plain_cap : 32768;
        while (next < tls->plain_len + len) next *= 2;
        unsigned char *grown = (unsigned char *)realloc(tls->plain_buf, next);
        if (!grown) return -1;
        tls->plain_buf = grown;
        tls->plain_cap = next;
    }
    memcpy(tls->plain_buf + tls->plain_len, data, len);
    tls->plain_len += len;
    return 0;
}

static int run_handshake_loop(PB_TLS_CLIENT *tls, BOOL initial, DWORD *out_error)
{
    DWORD flags = ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT |
                  ISC_REQ_CONFIDENTIALITY | ISC_REQ_EXTENDED_ERROR |
                  ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM;
    if (tls->insecure) {
        flags |= ISC_REQ_MANUAL_CRED_VALIDATION;
    }
    DWORD out_flags = 0;
    SECURITY_STATUS sc = SEC_I_CONTINUE_NEEDED;

    if (initial) {
        SecBuffer out_buf[1] = { { 0, SECBUFFER_TOKEN, NULL } };
        SecBufferDesc out_desc = { SECBUFFER_VERSION, 1, out_buf };
        sc = InitializeSecurityContextA(
            &tls->cred, NULL,
            tls->sni_host[0] ? tls->sni_host : NULL,
            flags, 0, SECURITY_NATIVE_DREP, NULL, 0,
            &tls->ctx, &out_desc, &out_flags, NULL);
        if (sc != SEC_I_CONTINUE_NEEDED && sc != SEC_E_OK) {
            if (out_error) *out_error = (DWORD)sc;
            return -1;
        }
        tls->has_ctx = TRUE;
        if (out_buf[0].cbBuffer > 0 && out_buf[0].pvBuffer != NULL) {
            int rc = send_raw_all(tls->sock, (const unsigned char *)out_buf[0].pvBuffer, (int)out_buf[0].cbBuffer, out_error);
            FreeContextBuffer(out_buf[0].pvBuffer);
            if (rc != 0) return -1;
        }
    }

    while (sc == SEC_I_CONTINUE_NEEDED || sc == SEC_E_INCOMPLETE_MESSAGE) {
        if (tls->in_len == 0 || sc == SEC_E_INCOMPLETE_MESSAGE) {
            if (ensure_in_capacity(tls, tls->in_len + 16384) != 0) {
                if (out_error) *out_error = WSAENOBUFS;
                return -1;
            }
            int got = recv(tls->sock, (char *)(tls->in_buf + tls->in_len), (int)(tls->in_cap - tls->in_len), 0);
            if (got <= 0) {
                if (out_error) *out_error = (got == SOCKET_ERROR) ? (DWORD)WSAGetLastError() : ERROR_GRACEFUL_DISCONNECT;
                return -1;
            }
            tls->in_len += (size_t)got;
        }

        SecBuffer in_bufs[2] = {
            { (unsigned long)tls->in_len, SECBUFFER_TOKEN, tls->in_buf },
            { 0, SECBUFFER_EMPTY, NULL }
        };
        SecBufferDesc in_desc = { SECBUFFER_VERSION, 2, in_bufs };
        SecBuffer out_bufs[1] = { { 0, SECBUFFER_TOKEN, NULL } };
        SecBufferDesc out_desc = { SECBUFFER_VERSION, 1, out_bufs };

        sc = InitializeSecurityContextA(
            &tls->cred, &tls->ctx,
            tls->sni_host[0] ? tls->sni_host : NULL,
            flags, 0, SECURITY_NATIVE_DREP, &in_desc, 0,
            NULL, &out_desc, &out_flags, NULL);

        if (out_bufs[0].cbBuffer > 0 && out_bufs[0].pvBuffer != NULL) {
            int send_rc = send_raw_all(tls->sock, (const unsigned char *)out_bufs[0].pvBuffer, (int)out_bufs[0].cbBuffer, out_error);
            FreeContextBuffer(out_bufs[0].pvBuffer);
            if (send_rc != 0) return -1;
        }

        if (sc == SEC_E_INCOMPLETE_MESSAGE) {
            continue;
        }

        if (in_bufs[1].BufferType == SECBUFFER_EXTRA && in_bufs[1].cbBuffer > 0) {
            size_t extra = in_bufs[1].cbBuffer;
            memmove(tls->in_buf, tls->in_buf + (tls->in_len - extra), extra);
            tls->in_len = extra;
        } else {
            tls->in_len = 0;
        }

        if (FAILED(sc) && sc != SEC_E_INCOMPLETE_MESSAGE) {
            if (out_error) *out_error = (DWORD)sc;
            return -1;
        }
    }

    if (sc != SEC_E_OK) {
        if (out_error) *out_error = (DWORD)sc;
        return -1;
    }

    sc = QueryContextAttributesA(&tls->ctx, SECPKG_ATTR_STREAM_SIZES, &tls->sizes);
    if (sc != SEC_E_OK) {
        if (out_error) *out_error = (DWORD)sc;
        return -1;
    }
    return 0;
}

PB_TLS_CLIENT *pb_tls_client_open(SOCKET sock, const char *sni_host, BOOL insecure, DWORD *out_error)
{
    if (out_error) *out_error = 0;
    if (sock == INVALID_SOCKET) return NULL;
    PB_TLS_CLIENT *tls = (PB_TLS_CLIENT *)calloc(1, sizeof(PB_TLS_CLIENT));
    if (!tls) return NULL;
    tls->sock = sock;
    tls->insecure = insecure;
    if (sni_host) strncpy_s(tls->sni_host, sizeof(tls->sni_host), sni_host, _TRUNCATE);

    SCHANNEL_CRED cred;
    ZeroMemory(&cred, sizeof(cred));
    cred.dwVersion = SCHANNEL_CRED_VERSION;
    cred.dwFlags = SCH_CRED_NO_DEFAULT_CREDS | SCH_USE_STRONG_CRYPTO;
    if (insecure) {
        cred.dwFlags |= SCH_CRED_MANUAL_CRED_VALIDATION | SCH_CRED_IGNORE_NO_REVOCATION_CHECK | SCH_CRED_IGNORE_REVOCATION_OFFLINE;
    } else {
        cred.dwFlags |= SCH_CRED_AUTO_CRED_VALIDATION;
    }

    SECURITY_STATUS sc = AcquireCredentialsHandleA(
        NULL, UNISP_NAME_A, SECPKG_CRED_OUTBOUND, NULL, &cred, NULL, NULL, &tls->cred, NULL);
    if (sc != SEC_E_OK) {
        if (out_error) *out_error = (DWORD)sc;
        pb_tls_client_close(tls);
        return NULL;
    }
    tls->has_cred = TRUE;

    if (run_handshake_loop(tls, TRUE, out_error) != 0) {
        pb_tls_client_close(tls);
        return NULL;
    }
    return tls;
}

int pb_tls_client_send_all(PB_TLS_CLIENT *tls, const char *data, int len)
{
    if (!tls || !tls->has_ctx) return -1;
    if (len <= 0) return 0;

    int offset = 0;
    size_t max_chunk = tls->sizes.cbMaximumMessage ? tls->sizes.cbMaximumMessage : 16384;
    size_t io_cap = tls->sizes.cbHeader + max_chunk + tls->sizes.cbTrailer;
    unsigned char *io_buf = (unsigned char *)malloc(io_cap);
    if (!io_buf) return -1;

    while (offset < len) {
        int chunk = (int)max_chunk;
        if (len - offset < chunk) chunk = len - offset;

        SecBuffer bufs[4] = {
            { tls->sizes.cbHeader, SECBUFFER_STREAM_HEADER, io_buf },
            { (unsigned long)chunk, SECBUFFER_DATA, io_buf + tls->sizes.cbHeader },
            { tls->sizes.cbTrailer, SECBUFFER_STREAM_TRAILER, io_buf + tls->sizes.cbHeader + chunk },
            { 0, SECBUFFER_EMPTY, NULL }
        };
        memcpy(bufs[1].pvBuffer, data + offset, (size_t)chunk);
        SecBufferDesc desc = { SECBUFFER_VERSION, 4, bufs };
        SECURITY_STATUS sc = EncryptMessage(&tls->ctx, 0, &desc, 0);
        if (sc != SEC_E_OK) {
            free(io_buf);
            return -1;
        }
        int total_wire = (int)(bufs[0].cbBuffer + bufs[1].cbBuffer + bufs[2].cbBuffer);
        if (send_raw_all(tls->sock, io_buf, total_wire, NULL) != 0) {
            free(io_buf);
            return -1;
        }
        offset += chunk;
    }
    SecureZeroMemory(io_buf, io_cap);
    free(io_buf);
    return 0;
}

static int decrypt_available(PB_TLS_CLIENT *tls)
{
    while (tls->in_len > 0) {
        SecBuffer bufs[4] = {
            { (unsigned long)tls->in_len, SECBUFFER_DATA, tls->in_buf },
            { 0, SECBUFFER_EMPTY, NULL },
            { 0, SECBUFFER_EMPTY, NULL },
            { 0, SECBUFFER_EMPTY, NULL }
        };
        SecBufferDesc desc = { SECBUFFER_VERSION, 4, bufs };
        SECURITY_STATUS sc = DecryptMessage(&tls->ctx, &desc, 0, NULL);

        if (sc == SEC_E_INCOMPLETE_MESSAGE) {
            tls->need_more_net = TRUE;
            return 0;
        }

        unsigned char *data_ptr = NULL;
        size_t data_len = 0;
        unsigned char *extra_ptr = NULL;
        size_t extra_len = 0;
        for (int i = 0; i < 4; i++) {
            if (bufs[i].BufferType == SECBUFFER_DATA && bufs[i].cbBuffer > 0) {
                data_ptr = (unsigned char *)bufs[i].pvBuffer;
                data_len = bufs[i].cbBuffer;
            } else if (bufs[i].BufferType == SECBUFFER_EXTRA && bufs[i].cbBuffer > 0) {
                extra_ptr = (unsigned char *)bufs[i].pvBuffer;
                extra_len = bufs[i].cbBuffer;
            }
        }

        if (sc == SEC_E_OK || sc == SEC_I_RENEGOTIATE || sc == SEC_I_CONTEXT_EXPIRED) {
            if (data_ptr && data_len > 0) {
                if (append_plain(tls, data_ptr, data_len) != 0) return -1;
            }
            if (extra_ptr && extra_len > 0) {
                memmove(tls->in_buf, extra_ptr, extra_len);
                tls->in_len = extra_len;
                tls->need_more_net = FALSE;
            } else {
                tls->in_len = 0;
                tls->need_more_net = FALSE;
            }
            if (sc == SEC_I_CONTEXT_EXPIRED) {
                tls->peer_closed = TRUE;
                return 0;
            }
            if (sc == SEC_I_RENEGOTIATE) {
                if (run_handshake_loop(tls, FALSE, NULL) != 0) return -1;
                continue;
            }
            if (tls->plain_len > tls->plain_off) return 1;
            continue;
        }
        return -1;
    }
    return 0;
}

int pb_tls_client_recv(PB_TLS_CLIENT *tls, char *buf, int cap)
{
    if (!tls || !buf || cap <= 0) return -1;
    for (;;) {
        if (tls->plain_len > tls->plain_off) {
            size_t avail = tls->plain_len - tls->plain_off;
            int take = ((size_t)cap < avail) ? cap : (int)avail;
            memcpy(buf, tls->plain_buf + tls->plain_off, (size_t)take);
            tls->plain_off += (size_t)take;
            if (tls->plain_off == tls->plain_len) {
                tls->plain_off = 0;
                tls->plain_len = 0;
            }
            return take;
        }
        if (tls->in_len > 0 && !tls->need_more_net) {
            int d = decrypt_available(tls);
            if (d < 0) return -1;
            if (tls->plain_len > tls->plain_off) continue;
        }
        if (tls->peer_closed) return 0;

        if (ensure_in_capacity(tls, tls->in_len + 16384) != 0) return -1;
        int got = recv(tls->sock, (char *)(tls->in_buf + tls->in_len), (int)(tls->in_cap - tls->in_len), 0);
        if (got == 0) {
            tls->peer_closed = TRUE;
            return 0;
        }
        if (got == SOCKET_ERROR) return -1;
        tls->in_len += (size_t)got;
        tls->need_more_net = FALSE;
    }
}

int pb_tls_client_http_connect(PB_TLS_CLIENT *tls, const char *host, UINT16 port, const char *username, const char *password, int *out_status, DWORD *out_error)
{
    char request[2048], response[8193];
    int used = 0;
    if (out_status) *out_status = 0;
    if (out_error) *out_error = 0;
    int length = ld_tls_format_connect_request(host, port, username, password, request, sizeof(request));
    if (length <= 0) return -1;
    int send_rc = pb_tls_client_send_all(tls, request, length);
    SecureZeroMemory(request, sizeof(request));
    if (send_rc != 0) return -1;

    while (used < (int)sizeof(response) - 1) {
        int n = pb_tls_client_recv(tls, response + used, 1);
        if (n != 1) {
            if (out_error) *out_error = (DWORD)WSAGetLastError();
            return -1;
        }
        used++;
        if (used >= 4 && memcmp(response + used - 4, "\r\n\r\n", 4) == 0) {
            response[used] = 0;
            int code = ld_tls_parse_http_status(response, used);
            if (code < 0) return -1;
            if (out_status) *out_status = code;
            return code == 200 ? 0 : -1;
        }
    }
    return -1;
}

void pb_tls_client_relay(PB_TLS_CLIENT *tls, SOCKET client_sock)
{
    if (!tls || client_sock == INVALID_SOCKET) return;
    char *buf = (char *)malloc(32768);
    if (!buf) return;

    for (;;) {
        // Flush any buffered plaintext or complete TLS records before waiting on OS sockets.
        if (tls->plain_len > tls->plain_off) {
            size_t avail = tls->plain_len - tls->plain_off;
            if (send_raw_all(client_sock, tls->plain_buf + tls->plain_off, (int)avail, NULL) != 0) break;
            tls->plain_off = 0;
            tls->plain_len = 0;
        }
        if (tls->in_len > 0 && !tls->need_more_net) {
            if (decrypt_available(tls) < 0) break;
            if (tls->plain_len > tls->plain_off) continue;
        }
        if (tls->peer_closed) break;

        fd_set reads;
        FD_ZERO(&reads);
        FD_SET(client_sock, &reads);
        FD_SET(tls->sock, &reads);
        struct timeval tv = { 300, 0 };
        int ready = select(0, &reads, NULL, NULL, &tv);
        if (ready <= 0) {
            if (ready < 0) break;
            continue;
        }

        if (FD_ISSET(client_sock, &reads)) {
            int n = recv(client_sock, buf, 32768, 0);
            if (n <= 0) break;
            if (pb_tls_client_send_all(tls, buf, n) != 0) break;
        }
        if (FD_ISSET(tls->sock, &reads)) {
            if (ensure_in_capacity(tls, tls->in_len + 32768) != 0) break;
            int got = recv(tls->sock, (char *)(tls->in_buf + tls->in_len), (int)(tls->in_cap - tls->in_len), 0);
            if (got <= 0) break;
            tls->in_len += (size_t)got;
            tls->need_more_net = FALSE;
        }
    }
    free(buf);
}

void pb_tls_client_close(PB_TLS_CLIENT *tls)
{
    if (!tls) return;
    if (tls->has_ctx) DeleteSecurityContext(&tls->ctx);
    if (tls->has_cred) FreeCredentialsHandle(&tls->cred);
    if (tls->in_buf) { SecureZeroMemory(tls->in_buf, tls->in_cap); free(tls->in_buf); }
    if (tls->plain_buf) { SecureZeroMemory(tls->plain_buf, tls->plain_cap); free(tls->plain_buf); }
    free(tls);
}
