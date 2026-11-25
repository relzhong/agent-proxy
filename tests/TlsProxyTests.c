#define SECURITY_WIN32
#include "pb_tls.h"
#include "pb_schannel.h"
#include <winsock2.h>
#include <windows.h>
#include <wincrypt.h>
#include <security.h>
#include <schannel.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static int checks;
#define CHECK(x) do { if (!(x)) { fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x); exit(1); } checks++; } while(0)
static void put16(unsigned char *b, size_t n) { b[0]=(unsigned char)(n>>8); b[1]=(unsigned char)n; }
static size_t client_hello(unsigned char *b,const char *host)
{
    size_t n=strlen(host),p=9;
    b[0]=22;b[1]=3;b[2]=1;
    b[5]=1;b[6]=0;
    b[p++]=3;b[p++]=3;memset(b+p,0,32);p+=32;
    b[p++]=0;put16(b+p,2);p+=2;b[p++]=0x13;b[p++]=1;
    b[p++]=1;b[p++]=0;put16(b+p,n+9);p+=2;
    put16(b+p,0);p+=2;put16(b+p,n+5);p+=2;put16(b+p,n+3);p+=2;
    b[p++]=0;put16(b+p,n);p+=2;memcpy(b+p,host,n);p+=n;
    put16(b+3,p-5);put16(b+7,p-9);
    return p;
}
static void pair(SOCKET *a,SOCKET *b)
{
    SOCKET listener=socket(AF_INET,SOCK_STREAM,0);CHECK(listener!=INVALID_SOCKET);
    struct sockaddr_in address;memset(&address,0,sizeof(address));address.sin_family=AF_INET;address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
    CHECK(bind(listener,(struct sockaddr*)&address,sizeof(address))==0);CHECK(listen(listener,1)==0);
    int len=sizeof(address);CHECK(getsockname(listener,(struct sockaddr*)&address,&len)==0);
    *a=socket(AF_INET,SOCK_STREAM,0);CHECK(connect(*a,(struct sockaddr*)&address,len)==0);
    *b=accept(listener,NULL,NULL);CHECK(*b!=INVALID_SOCKET);closesocket(listener);
    DWORD timeout=2000;setsockopt(*a,SOL_SOCKET,SO_RCVTIMEO,(char*)&timeout,sizeof(timeout));setsockopt(*b,SOL_SOCKET,SO_RCVTIMEO,(char*)&timeout,sizeof(timeout));
}
typedef struct {SOCKET socket;unsigned char bytes[1024];size_t size;int status;int matched;const char *expected;} Sender;
static DWORD WINAPI fragmented_hello(LPVOID arg)
{
    Sender *s=(Sender*)arg;send(s->socket,(char*)s->bytes,3,0);Sleep(30);send(s->socket,(char*)s->bytes+3,(int)s->size-3,0);return 0;
}
static DWORD WINAPI proxy_reply(LPVOID arg)
{
    Sender *s=(Sender*)arg;char request[1024];int used=0;
    while(used<(int)sizeof(request)-1){if(recv(s->socket,request+used,1,0)!=1)return 1;used++;if(used>=4&&!memcmp(request+used-4,"\r\n\r\n",4))break;}
    request[used]=0;const char *want=s->expected?s->expected:"CONNECT codex-cloud-backend.chatgpt.com:443 HTTP/1.1\r\nHost: codex-cloud-backend.chatgpt.com:443\r\nProxy-Connection: keep-alive\r\n\r\n";s->matched=!strcmp(request,want);
    char reply[128];sprintf_s(reply,sizeof(reply),"HTTP/1.1 %d Test\r\n\r\nZ",s->status);
    send(s->socket,reply,7,0);Sleep(10);send(s->socket,reply+7,(int)strlen(reply)-7,0);return 0;
}
static PCCERT_CONTEXT create_self_signed_cert(HCRYPTPROV *out_prov, HCRYPTKEY *out_key, wchar_t *container_name, size_t container_cap)
{
    _snwprintf_s(container_name, container_cap, _TRUNCATE, L"AgentProxyTlsTest-%lu", GetCurrentProcessId());
    CryptAcquireContextW(out_prov, container_name, NULL, PROV_RSA_AES, CRYPT_DELETEKEYSET);
    if (!CryptAcquireContextW(out_prov, container_name, NULL, PROV_RSA_AES, CRYPT_NEWKEYSET | CRYPT_SILENT))
        return NULL;
    if (!CryptGenKey(*out_prov, AT_KEYEXCHANGE, (2048 << 16) | CRYPT_EXPORTABLE, out_key)) {
        CryptReleaseContext(*out_prov, 0);
        return NULL;
    }
    CERT_NAME_BLOB subject = {0};
    if (!CertStrToNameW(X509_ASN_ENCODING, L"CN=localhost", CERT_X500_NAME_STR, NULL, NULL, &subject.cbData, NULL))
        return NULL;
    subject.pbData = (BYTE*)malloc(subject.cbData);
    if (!subject.pbData || !CertStrToNameW(X509_ASN_ENCODING, L"CN=localhost", CERT_X500_NAME_STR, NULL, subject.pbData, &subject.cbData, NULL)) {
        free(subject.pbData);
        return NULL;
    }
    CRYPT_KEY_PROV_INFO kpi;
    memset(&kpi, 0, sizeof(kpi));
    kpi.pwszContainerName = container_name;
    kpi.dwProvType = PROV_RSA_AES;
    kpi.dwKeySpec = AT_KEYEXCHANGE;
    CRYPT_ALGORITHM_IDENTIFIER sigAlg;
    memset(&sigAlg, 0, sizeof(sigAlg));
    sigAlg.pszObjId = szOID_RSA_SHA256RSA;
    PCCERT_CONTEXT cert = CertCreateSelfSignCertificate(*out_prov, &subject, 0, &kpi, &sigAlg, NULL, NULL, NULL);
    free(subject.pbData);
    return cert;
}

typedef struct {
    SOCKET sock;
    PCCERT_CONTEXT cert;
    int expected_auth;
    int handshake_ok;
    int request_matched;
    int relay_echo_ok;
} TlsServerArg;

static DWORD WINAPI tls_proxy_server(LPVOID arg)
{
    TlsServerArg *s = (TlsServerArg*)arg;
    SCHANNEL_CRED scred;
    memset(&scred, 0, sizeof(scred));
    scred.dwVersion = SCHANNEL_CRED_VERSION;
    scred.cCreds = 1;
    scred.paCred = &s->cert;
    CredHandle cred;
    CtxtHandle ctx;
    BOOL has_ctx = FALSE;
    if (AcquireCredentialsHandleA(NULL, UNISP_NAME_A, SECPKG_CRED_INBOUND, NULL, &scred, NULL, NULL, &cred, NULL) != SEC_E_OK)
        return 1;

    unsigned char inbuf[32768];
    size_t inlen = 0;
    SECURITY_STATUS sc = SEC_I_CONTINUE_NEEDED;
    DWORD outFlags = 0;
    while (sc == SEC_I_CONTINUE_NEEDED || sc == SEC_E_INCOMPLETE_MESSAGE) {
        if (inlen == 0 || sc == SEC_E_INCOMPLETE_MESSAGE) {
            int got = recv(s->sock, (char*)inbuf + inlen, (int)(sizeof(inbuf) - inlen), 0);
            if (got <= 0) goto cleanup;
            inlen += (size_t)got;
        }
        SecBuffer ib[2] = { { (unsigned long)inlen, SECBUFFER_TOKEN, inbuf }, { 0, SECBUFFER_EMPTY, NULL } };
        SecBufferDesc id = { SECBUFFER_VERSION, 2, ib };
        SecBuffer ob[1] = { { 0, SECBUFFER_TOKEN, NULL } };
        SecBufferDesc od = { SECBUFFER_VERSION, 1, ob };
        sc = AcceptSecurityContext(&cred, has_ctx ? &ctx : NULL, &id,
            ASC_REQ_SEQUENCE_DETECT | ASC_REQ_REPLAY_DETECT | ASC_REQ_CONFIDENTIALITY | ASC_REQ_ALLOCATE_MEMORY | ASC_REQ_STREAM,
            SECURITY_NATIVE_DREP, has_ctx ? NULL : &ctx, &od, &outFlags, NULL);
        if (sc == SEC_I_CONTINUE_NEEDED || sc == SEC_E_OK) has_ctx = TRUE;
        if (ob[0].cbBuffer > 0 && ob[0].pvBuffer) {
            int sent = 0;
            while (sent < (int)ob[0].cbBuffer) {
                int w = send(s->sock, (const char*)ob[0].pvBuffer + sent, (int)ob[0].cbBuffer - sent, 0);
                if (w <= 0) { FreeContextBuffer(ob[0].pvBuffer); goto cleanup; }
                sent += w;
            }
            FreeContextBuffer(ob[0].pvBuffer);
        }
        if (sc == SEC_E_INCOMPLETE_MESSAGE) continue;
        if (ib[1].BufferType == SECBUFFER_EXTRA && ib[1].cbBuffer > 0) {
            memmove(inbuf, inbuf + (inlen - ib[1].cbBuffer), ib[1].cbBuffer);
            inlen = ib[1].cbBuffer;
        } else inlen = 0;
        if (FAILED(sc) && sc != SEC_E_INCOMPLETE_MESSAGE) goto cleanup;
    }
    if (sc != SEC_E_OK) goto cleanup;
    s->handshake_ok = 1;

    SecPkgContext_StreamSizes sizes;
    if (QueryContextAttributesA(&ctx, SECPKG_ATTR_STREAM_SIZES, &sizes) != SEC_E_OK) goto cleanup;

    char req[4096];
    int req_len = 0;
    while (!strstr(req, "\r\n\r\n")) {
        if (inlen == 0) {
            int got = recv(s->sock, (char*)inbuf, sizeof(inbuf), 0);
            if (got <= 0) goto cleanup;
            inlen = (size_t)got;
        }
        SecBuffer db[4] = { { (unsigned long)inlen, SECBUFFER_DATA, inbuf }, {0}, {0}, {0} };
        SecBufferDesc dd = { SECBUFFER_VERSION, 4, db };
        sc = DecryptMessage(&ctx, &dd, 0, NULL);
        if (sc == SEC_E_INCOMPLETE_MESSAGE) {
            int got = recv(s->sock, (char*)inbuf + inlen, (int)(sizeof(inbuf) - inlen), 0);
            if (got <= 0) goto cleanup;
            inlen += (size_t)got;
            continue;
        }
        if (sc != SEC_E_OK) goto cleanup;
        for (int i = 0; i < 4; i++) {
            if (db[i].BufferType == SECBUFFER_DATA && req_len + (int)db[i].cbBuffer < (int)sizeof(req) - 1) {
                memcpy(req + req_len, db[i].pvBuffer, db[i].cbBuffer);
                req_len += (int)db[i].cbBuffer;
                req[req_len] = 0;
            }
        }
        inlen = 0;
        for (int i = 0; i < 4; i++) {
            if (db[i].BufferType == SECBUFFER_EXTRA && db[i].cbBuffer > 0) {
                memmove(inbuf, db[i].pvBuffer, db[i].cbBuffer);
                inlen = db[i].cbBuffer;
            }
        }
    }
    const char *want = s->expected_auth
        ? "CONNECT api.openai.com:443 HTTP/1.1\r\nHost: api.openai.com:443\r\nProxy-Authorization: Basic dXNlcjpwYXNz\r\nProxy-Connection: keep-alive\r\n\r\n"
        : "CONNECT api.openai.com:443 HTTP/1.1\r\nHost: api.openai.com:443\r\nProxy-Connection: keep-alive\r\n\r\n";
    s->request_matched = !strcmp(req, want);

    // Send 200 Connection Established + an early tunneled sentinel byte 'Z' in one TLS record.
    {
        const char *ok_resp = "HTTP/1.1 200 Connection Established\r\n\r\nZ";
        int rlen = (int)strlen(ok_resp);
        unsigned char *wire = (unsigned char*)malloc(sizes.cbHeader + rlen + sizes.cbTrailer);
        SecBuffer sb[4] = {
            { sizes.cbHeader, SECBUFFER_STREAM_HEADER, wire },
            { (unsigned long)rlen, SECBUFFER_DATA, wire + sizes.cbHeader },
            { sizes.cbTrailer, SECBUFFER_STREAM_TRAILER, wire + sizes.cbHeader + rlen },
            { 0, SECBUFFER_EMPTY, NULL }
        };
        memcpy(sb[1].pvBuffer, ok_resp, (size_t)rlen);
        SecBufferDesc sd = { SECBUFFER_VERSION, 4, sb };
        if (EncryptMessage(&ctx, 0, &sd, 0) == SEC_E_OK) {
            int tot = (int)(sb[0].cbBuffer + sb[1].cbBuffer + sb[2].cbBuffer);
            send(s->sock, (const char*)wire, tot, 0);
        }
        free(wire);
    }

    // Read relayed payload ("PING") and echo back ("PONG")
    if (s->expected_auth) {
        char payload[128] = {0};
        int plen = 0;
        while (plen < 4) {
            if (inlen == 0) {
                int got = recv(s->sock, (char*)inbuf, sizeof(inbuf), 0);
                if (got <= 0) goto cleanup;
                inlen = (size_t)got;
            }
            SecBuffer db[4] = { { (unsigned long)inlen, SECBUFFER_DATA, inbuf }, {0}, {0}, {0} };
            SecBufferDesc dd = { SECBUFFER_VERSION, 4, db };
            sc = DecryptMessage(&ctx, &dd, 0, NULL);
            if (sc == SEC_E_INCOMPLETE_MESSAGE) {
                int got = recv(s->sock, (char*)inbuf + inlen, (int)(sizeof(inbuf) - inlen), 0);
                if (got <= 0) goto cleanup;
                inlen += (size_t)got;
                continue;
            }
            if (sc != SEC_E_OK) goto cleanup;
            for (int i = 0; i < 4; i++) {
                if (db[i].BufferType == SECBUFFER_DATA && plen + (int)db[i].cbBuffer < (int)sizeof(payload) - 1) {
                    memcpy(payload + plen, db[i].pvBuffer, db[i].cbBuffer);
                    plen += (int)db[i].cbBuffer;
                    payload[plen] = 0;
                }
            }
            inlen = 0;
        }
        if (!strcmp(payload, "PING")) {
            const char *pong = "PONG";
            unsigned char *wire = (unsigned char*)malloc(sizes.cbHeader + 4 + sizes.cbTrailer);
            SecBuffer sb[4] = {
                { sizes.cbHeader, SECBUFFER_STREAM_HEADER, wire },
                { 4, SECBUFFER_DATA, wire + sizes.cbHeader },
                { sizes.cbTrailer, SECBUFFER_STREAM_TRAILER, wire + sizes.cbHeader + 4 },
                { 0, SECBUFFER_EMPTY, NULL }
            };
            memcpy(sb[1].pvBuffer, pong, 4);
            SecBufferDesc sd = { SECBUFFER_VERSION, 4, sb };
            if (EncryptMessage(&ctx, 0, &sd, 0) == SEC_E_OK) {
                send(s->sock, (const char*)wire, (int)(sb[0].cbBuffer + sb[1].cbBuffer + sb[2].cbBuffer), 0);
                s->relay_echo_ok = 1;
            }
            free(wire);
        }
    }

cleanup:
    if (has_ctx) DeleteSecurityContext(&ctx);
    FreeCredentialsHandle(&cred);
    return 0;
}

typedef struct { PB_TLS_CLIENT *tls; SOCKET client; } RelayThreadArg;
static DWORD WINAPI run_tls_relay(LPVOID arg)
{
    RelayThreadArg *r = (RelayThreadArg*)arg;
    pb_tls_client_relay(r->tls, r->client);
    closesocket(r->client);
    return 0;
}

static void schannel_tests(void)
{
    HCRYPTPROV prov = 0; HCRYPTKEY key = 0; wchar_t container[64];
    PCCERT_CONTEXT cert = create_self_signed_cert(&prov, &key, container, 64);
    CHECK(cert != NULL);

    // 1. Strict cert validation must REJECT an untrusted self-signed cert
    {
        SOCKET a, b; pair(&a, &b);
        TlsServerArg srv = { b, cert, 0, 0, 0, 0 };
        HANDLE th = CreateThread(NULL, 0, tls_proxy_server, &srv, 0, NULL);
        CHECK(th != NULL);
        DWORD tls_err = 0;
        PB_TLS_CLIENT *tls = pb_tls_client_open(a, "localhost", FALSE, &tls_err);
        CHECK(tls == NULL);
        CHECK(tls_err != 0);
        closesocket(a);
        WaitForSingleObject(th, 2000); CloseHandle(th);
        closesocket(b);
    }

    // 2. Self-signed trust enabled (insecure=TRUE) succeeds, sends Basic auth CONNECT, preserves 'Z', and relays bidirectionally
    {
        SOCKET a, b, app_client, app_relay;
        pair(&a, &b);
        pair(&app_client, &app_relay);
        TlsServerArg srv = { b, cert, 1, 0, 0, 0 };
        HANDLE th = CreateThread(NULL, 0, tls_proxy_server, &srv, 0, NULL);
        CHECK(th != NULL);
        DWORD tls_err = 0;
        PB_TLS_CLIENT *tls = pb_tls_client_open(a, "localhost", TRUE, &tls_err);
        CHECK(tls != NULL);
        int status = 0; DWORD err = 0;
        CHECK(pb_tls_client_http_connect(tls, "api.openai.com", 443, "user", "pass", &status, &err) == 0);
        CHECK(status == 200);
        RelayThreadArg rarg = { tls, app_relay };
        HANDLE rth = CreateThread(NULL, 0, run_tls_relay, &rarg, 0, NULL);
        CHECK(rth != NULL);
        char first = 0;
        CHECK(recv(app_client, &first, 1, 0) == 1 && first == 'Z');
        CHECK(send(app_client, "PING", 4, 0) == 4);
        char pong[5] = {0};
        CHECK(recv(app_client, pong, 4, MSG_WAITALL) == 4 && !strcmp(pong, "PONG"));
        closesocket(app_client);
        WaitForSingleObject(rth, 2000); CloseHandle(rth);
        WaitForSingleObject(th, 2000); CloseHandle(th);
        CHECK(srv.handshake_ok && srv.request_matched && srv.relay_echo_ok);
        pb_tls_client_close(tls);
        closesocket(a);
        closesocket(b);
    }

    CertFreeCertificateContext(cert);
    if (key) CryptDestroyKey(key);
    if (prov) CryptReleaseContext(prov, 0);
    CryptAcquireContextW(&prov, container, NULL, PROV_RSA_AES, CRYPT_DELETEKEYSET);
}

static void tests(void)
{
    unsigned char bytes[1024],split[1100];char host[254];
    size_t size=client_hello(bytes,"CoDeX-cloud-backend.ChatGPT.com");
    CHECK(ld_tls_server_name(bytes,size,host,sizeof(host))==1);CHECK(!strcmp(host,"codex-cloud-backend.chatgpt.com"));CHECK(ld_tls_is_openai_host(host));
    for(size_t i=0;i<size;i++)CHECK(ld_tls_server_name(bytes,i,host,sizeof(host))==-1);
    for(size_t part=1;part<size-5;part++){
        memcpy(split,bytes,5);put16(split+3,part);memcpy(split+5,bytes+5,part);
        memcpy(split+5+part,bytes,5);put16(split+8+part,size-5-part);memcpy(split+10+part,bytes+5+part,size-5-part);
        CHECK(ld_tls_server_name(split,size+5,host,sizeof(host))==1);CHECK(!strcmp(host,"codex-cloud-backend.chatgpt.com"));
    }
    const char *invalid[]={"chatgpt.com\r\nX: y","-chatgpt.com","chatgpt..com","chatgpt.com-","chatgpt.com.",""};
    for(size_t i=0;i<sizeof(invalid)/sizeof(invalid[0]);i++){size=client_hello(bytes,invalid[i]);CHECK(ld_tls_server_name(bytes,size,host,sizeof(host))==0);CHECK(host[0]==0);}
    CHECK(!ld_tls_is_openai_host("chatgpt.com.evil"));CHECK(!ld_tls_is_openai_host("notchatgpt.com"));CHECK(!ld_tls_is_openai_host("localhost"));CHECK(!ld_tls_is_openai_host("127.0.0.1"));CHECK(!ld_tls_is_openai_host("example.com"));CHECK(ld_tls_is_openai_host("api.openai.com"));
    size=client_hello(bytes,"chatgpt.com");CHECK(ld_tls_server_name(bytes,size,host,4)==0);
    bytes[0]=23;CHECK(ld_tls_server_name(bytes,size,host,sizeof(host))==0);bytes[0]=22;
    bytes[6]=1;CHECK(ld_tls_server_name(bytes,size,host,sizeof(host))==0);
    unsigned int random=7;for(int i=0;i<3000;i++){for(size_t j=0;j<sizeof(bytes);j++){random=random*1664525u+1013904223u;bytes[j]=(unsigned char)(random>>24);}int rc=ld_tls_server_name(bytes,(size_t)(i%1024),host,sizeof(host));CHECK(rc>=-1&&rc<=1);}
    SOCKET a,b;pair(&a,&b);Sender sender={0};sender.socket=a;sender.size=client_hello(sender.bytes,"chatgpt.com");
    HANDLE thread=CreateThread(NULL,0,fragmented_hello,&sender,0,NULL);CHECK(thread!=NULL);
    CHECK(ld_tls_peek_server_name(b,host,sizeof(host),500));CHECK(!strcmp(host,"chatgpt.com"));
    CHECK(WaitForSingleObject(thread,2000)==WAIT_OBJECT_0);CloseHandle(thread);
    CHECK(recv(b,(char*)bytes,(int)sender.size,MSG_WAITALL)==sender.size);CHECK(!memcmp(bytes,sender.bytes,sender.size));closesocket(a);closesocket(b);
    pair(&a,&b);ULONGLONG start=GetTickCount64();CHECK(!ld_tls_peek_server_name(b,host,sizeof(host),25));CHECK(GetTickCount64()-start<600);closesocket(a);closesocket(b);
    for(int code=200;code<=407;code+=207){pair(&a,&b);memset(&sender,0,sizeof(sender));sender.socket=b;sender.status=code;thread=CreateThread(NULL,0,proxy_reply,&sender,0,NULL);CHECK(thread!=NULL);int status;DWORD error;
        CHECK(ld_tls_http_connect(a,"codex-cloud-backend.chatgpt.com",443,&status,&error)==(code==200?0:-1));CHECK(status==code);CHECK(error==0);
        CHECK(WaitForSingleObject(thread,2000)==WAIT_OBJECT_0);CloseHandle(thread);CHECK(sender.matched);char sentinel;CHECK(recv(a,&sentinel,1,0)==1&&sentinel=='Z');closesocket(a);closesocket(b);
    }
    {pair(&a,&b);memset(&sender,0,sizeof(sender));sender.socket=b;sender.status=200;
        sender.expected="CONNECT codex-cloud-backend.chatgpt.com:443 HTTP/1.1\r\nHost: codex-cloud-backend.chatgpt.com:443\r\nProxy-Authorization: Basic dXNlcjpwYXNz\r\nProxy-Connection: keep-alive\r\n\r\n";
        thread=CreateThread(NULL,0,proxy_reply,&sender,0,NULL);CHECK(thread!=NULL);int status;DWORD error;
        CHECK(ld_tls_http_connect_auth(a,"codex-cloud-backend.chatgpt.com",443,"user","pass",&status,&error)==0);CHECK(status==200);CHECK(error==0);
        CHECK(WaitForSingleObject(thread,2000)==WAIT_OBJECT_0);CloseHandle(thread);CHECK(sender.matched);char sentinel;CHECK(recv(a,&sentinel,1,0)==1&&sentinel=='Z');closesocket(a);closesocket(b);
    }
    CHECK(ld_tls_http_connect(INVALID_SOCKET,"chatgpt.com\r\nHost:evil",443,NULL,NULL)==-1);
    CHECK(ld_tls_http_connect_auth(INVALID_SOCKET,"chatgpt.com",443,"bad\r\nuser","pass",NULL,NULL)==-1);
    CHECK(ld_tls_http_connect_auth(INVALID_SOCKET,"chatgpt.com",443,"user","bad\npass",NULL,NULL)==-1);
    schannel_tests();
    printf("TLS/domain tunnel checks passed: %d\n",checks);
}
/* Driver-free integration fixture. It exercises the production peek/CONNECT
   helpers with a real TLS client, leaving TLS verification to that client. */
static int live_relay(int listen_port,int proxy_port)
{
    SOCKET listener=socket(AF_INET,SOCK_STREAM,0),client,upstream;struct sockaddr_in address;memset(&address,0,sizeof(address));address.sin_family=AF_INET;address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);address.sin_port=htons((u_short)listen_port);
    if(bind(listener,(struct sockaddr*)&address,sizeof(address))||listen(listener,1))return 2;
    puts("READY");fflush(stdout);client=accept(listener,NULL,NULL);closesocket(listener);if(client==INVALID_SOCKET)return 3;
    char host[254];if(!ld_tls_peek_server_name(client,host,sizeof(host),2000)||!ld_tls_is_openai_host(host))return 4;
    upstream=socket(AF_INET,SOCK_STREAM,0);address.sin_port=htons((u_short)proxy_port);DWORD timeout=5000;setsockopt(upstream,SOL_SOCKET,SO_RCVTIMEO,(char*)&timeout,sizeof(timeout));
    if(connect(upstream,(struct sockaddr*)&address,sizeof(address)))return 5;int status;DWORD error;if(ld_tls_http_connect(upstream,host,443,&status,&error))return 6;
    printf("DOMAIN_TUNNEL status=%d\n",status);fflush(stdout);
    for(;;){fd_set reads;FD_ZERO(&reads);FD_SET(client,&reads);FD_SET(upstream,&reads);struct timeval wait={10,0};if(select(0,&reads,NULL,NULL,&wait)<=0)break;
        SOCKET sources[2]={client,upstream},destinations[2]={upstream,client};for(int i=0;i<2;i++)if(FD_ISSET(sources[i],&reads)){char buffer[16384];int n=recv(sources[i],buffer,sizeof(buffer),0);if(n<=0)goto done;int sent=0;while(sent<n){int k=send(destinations[i],buffer+sent,n-sent,0);if(k<=0)goto done;sent+=k;}}
    }
done:closesocket(client);closesocket(upstream);return 0;
}
int main(int argc,char **argv)
{
    WSADATA data;if(WSAStartup(MAKEWORD(2,2),&data))return 2;
    if(argc==4&&!strcmp(argv[1],"--relay")){int rc=live_relay(atoi(argv[2]),atoi(argv[3]));WSACleanup();return rc;}
    tests();WSACleanup();return 0;
}
