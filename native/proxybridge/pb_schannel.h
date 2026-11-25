#ifndef PB_SCHANNEL_H
#define PB_SCHANNEL_H

#include <winsock2.h>
#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct PB_TLS_CLIENT PB_TLS_CLIENT;

PB_TLS_CLIENT *pb_tls_client_open(SOCKET sock, const char *sni_host, BOOL insecure, DWORD *out_error);
int pb_tls_client_http_connect(PB_TLS_CLIENT *tls, const char *host, UINT16 port, const char *username, const char *password, int *out_status, DWORD *out_error);
int pb_tls_client_send_all(PB_TLS_CLIENT *tls, const char *data, int len);
int pb_tls_client_recv(PB_TLS_CLIENT *tls, char *buf, int cap);
void pb_tls_client_relay(PB_TLS_CLIENT *tls, SOCKET client_sock);
void pb_tls_client_close(PB_TLS_CLIENT *tls);

#ifdef __cplusplus
}
#endif

#endif
