#pragma once
#ifndef SECURITY_WIN32
#define SECURITY_WIN32
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <security.h>
#include <schannel.h>

#include <atomic>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "../core/Logger.hpp"

#ifndef SCH_USE_STRONG_CRYPTO
#define SCH_USE_STRONG_CRYPTO 0x00400000
#endif

namespace Network {

class TlsBridge {
public:
    struct RawOps {
        int (WSAAPI* sendFn)(SOCKET, const char*, int, int) = nullptr;
        int (WSAAPI* recvFn)(SOCKET, char*, int, int) = nullptr;
        int (WSAAPI* closeFn)(SOCKET) = nullptr;
        std::function<SOCKET()> dialUpstream;
    };

    static TlsBridge& Instance() {
        static TlsBridge inst;
        return inst;
    }

    bool EnsureStarted(const RawOps& ops, uint16_t* outPort) {
        if (!outPort) return false;
        std::lock_guard<std::mutex> lock(mtx_);
        ops_ = ops;
        if (listenSock_ != INVALID_SOCKET && port_ != 0 && !stop_.load()) {
            *outPort = port_;
            return true;
        }

        stop_.store(false);
        SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (s == INVALID_SOCKET) {
            Core::Logger::Error("HTTPS 代理桥: 创建本地监听 socket 失败, WSA=" + std::to_string(WSAGetLastError()));
            return false;
        }

        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        addr.sin_port = 0;
        if (bind(s, (const sockaddr*)&addr, sizeof(addr)) != 0 || listen(s, SOMAXCONN) != 0) {
            Core::Logger::Error("HTTPS 代理桥: bind/listen 失败, WSA=" + std::to_string(WSAGetLastError()));
            CloseRaw(s);
            return false;
        }

        sockaddr_in bound{};
        int boundLen = sizeof(bound);
        if (getsockname(s, (sockaddr*)&bound, &boundLen) != 0) {
            CloseRaw(s);
            return false;
        }

        listenSock_ = s;
        port_ = ntohs(bound.sin_port);
        *outPort = port_;

        std::thread([this, s]() { AcceptLoop(s); }).detach();
        Core::Logger::Info("HTTPS 代理桥已就绪: 127.0.0.1:" + std::to_string(port_));
        return true;
    }

    void Stop() {
        std::lock_guard<std::mutex> lock(mtx_);
        stop_.store(true);
        if (listenSock_ != INVALID_SOCKET) {
            CloseRaw(listenSock_);
            listenSock_ = INVALID_SOCKET;
        }
        port_ = 0;
    }

private:
    TlsBridge() = default;

    std::mutex mtx_;
    RawOps ops_;
    SOCKET listenSock_ = INVALID_SOCKET;
    uint16_t port_ = 0;
    std::atomic<bool> stop_{ false };

    int SendRaw(SOCKET s, const char* data, int len) {
        return ops_.sendFn ? ops_.sendFn(s, data, len, 0) : send(s, data, len, 0);
    }

    int RecvRaw(SOCKET s, char* buf, int cap) {
        return ops_.recvFn ? ops_.recvFn(s, buf, cap, 0) : recv(s, buf, cap, 0);
    }

    void CloseRaw(SOCKET s) {
        if (s == INVALID_SOCKET) return;
        if (ops_.closeFn) ops_.closeFn(s);
        else closesocket(s);
    }

    bool SendRawAll(SOCKET s, const unsigned char* data, int len) {
        int sent = 0;
        while (sent < len) {
            int n = SendRaw(s, (const char*)(data + sent), len - sent);
            if (n <= 0) return false;
            sent += n;
        }
        return true;
    }

    struct TlsSession {
        SOCKET sock = INVALID_SOCKET;
        CredHandle cred{};
        CtxtHandle ctx{};
        SecPkgContext_StreamSizes sizes{};
        bool hasCred = false;
        bool hasCtx = false;
        bool insecure = false;
        bool needMoreNet = false;
        bool peerClosed = false;
        std::string sniHost;
        std::vector<unsigned char> inBuf;
        std::vector<unsigned char> plainBuf;
        size_t plainOff = 0;
    };

    bool Handshake(TlsSession* tls, bool initial) {
        DWORD flags = ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT |
                      ISC_REQ_CONFIDENTIALITY | ISC_REQ_EXTENDED_ERROR |
                      ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM;
        if (tls->insecure) flags |= ISC_REQ_MANUAL_CRED_VALIDATION;

        DWORD outFlags = 0;
        SECURITY_STATUS sc = SEC_I_CONTINUE_NEEDED;
        SEC_CHAR* target = tls->sniHost.empty() ? nullptr : const_cast<SEC_CHAR*>(tls->sniHost.c_str());

        if (initial) {
            SecBuffer outBuf[1] = { { 0, SECBUFFER_TOKEN, nullptr } };
            SecBufferDesc outDesc = { SECBUFFER_VERSION, 1, outBuf };
            sc = InitializeSecurityContextA(
                &tls->cred, nullptr, target, flags, 0, SECURITY_NATIVE_DREP,
                nullptr, 0, &tls->ctx, &outDesc, &outFlags, nullptr);
            if (sc != SEC_I_CONTINUE_NEEDED && sc != SEC_E_OK) return false;
            tls->hasCtx = true;
            if (outBuf[0].cbBuffer > 0 && outBuf[0].pvBuffer) {
                bool sent = SendRawAll(tls->sock, (const unsigned char*)outBuf[0].pvBuffer, (int)outBuf[0].cbBuffer);
                FreeContextBuffer(outBuf[0].pvBuffer);
                if (!sent) return false;
            }
        }

        while (sc == SEC_I_CONTINUE_NEEDED || sc == SEC_E_INCOMPLETE_MESSAGE) {
            if (tls->inBuf.empty() || sc == SEC_E_INCOMPLETE_MESSAGE) {
                size_t oldSize = tls->inBuf.size();
                tls->inBuf.resize(oldSize + 16384);
                int got = RecvRaw(tls->sock, (char*)(tls->inBuf.data() + oldSize), 16384);
                if (got <= 0) return false;
                tls->inBuf.resize(oldSize + (size_t)got);
            }

            SecBuffer inBufs[2] = {
                { (unsigned long)tls->inBuf.size(), SECBUFFER_TOKEN, tls->inBuf.data() },
                { 0, SECBUFFER_EMPTY, nullptr }
            };
            SecBufferDesc inDesc = { SECBUFFER_VERSION, 2, inBufs };
            SecBuffer outBufs[1] = { { 0, SECBUFFER_TOKEN, nullptr } };
            SecBufferDesc outDesc = { SECBUFFER_VERSION, 1, outBufs };

            sc = InitializeSecurityContextA(
                &tls->cred, &tls->ctx, target, flags, 0, SECURITY_NATIVE_DREP,
                &inDesc, 0, nullptr, &outDesc, &outFlags, nullptr);

            if (outBufs[0].cbBuffer > 0 && outBufs[0].pvBuffer) {
                bool sent = SendRawAll(tls->sock, (const unsigned char*)outBufs[0].pvBuffer, (int)outBufs[0].cbBuffer);
                FreeContextBuffer(outBufs[0].pvBuffer);
                if (!sent) return false;
            }

            if (sc == SEC_E_INCOMPLETE_MESSAGE) continue;

            if (inBufs[1].BufferType == SECBUFFER_EXTRA && inBufs[1].cbBuffer > 0) {
                size_t extra = inBufs[1].cbBuffer;
                std::memmove(tls->inBuf.data(), tls->inBuf.data() + (tls->inBuf.size() - extra), extra);
                tls->inBuf.resize(extra);
            } else {
                tls->inBuf.clear();
            }

            if (FAILED(sc) && sc != SEC_E_INCOMPLETE_MESSAGE) return false;
        }

        if (sc != SEC_E_OK) return false;
        return QueryContextAttributesA(&tls->ctx, SECPKG_ATTR_STREAM_SIZES, &tls->sizes) == SEC_E_OK;
    }

    bool EncryptAndSend(TlsSession* tls, const char* data, int len) {
        int offset = 0;
        size_t maxChunk = tls->sizes.cbMaximumMessage ? tls->sizes.cbMaximumMessage : 16384;
        std::vector<unsigned char> wire(tls->sizes.cbHeader + maxChunk + tls->sizes.cbTrailer);
        while (offset < len) {
            int chunk = (int)maxChunk;
            if (len - offset < chunk) chunk = len - offset;
            SecBuffer bufs[4] = {
                { tls->sizes.cbHeader, SECBUFFER_STREAM_HEADER, wire.data() },
                { (unsigned long)chunk, SECBUFFER_DATA, wire.data() + tls->sizes.cbHeader },
                { tls->sizes.cbTrailer, SECBUFFER_STREAM_TRAILER, wire.data() + tls->sizes.cbHeader + chunk },
                { 0, SECBUFFER_EMPTY, nullptr }
            };
            std::memcpy(bufs[1].pvBuffer, data + offset, (size_t)chunk);
            SecBufferDesc desc = { SECBUFFER_VERSION, 4, bufs };
            if (EncryptMessage(&tls->ctx, 0, &desc, 0) != SEC_E_OK) return false;
            int wireLen = (int)(bufs[0].cbBuffer + bufs[1].cbBuffer + bufs[2].cbBuffer);
            if (!SendRawAll(tls->sock, wire.data(), wireLen)) return false;
            offset += chunk;
        }
        return true;
    }

    bool DecryptAvailable(TlsSession* tls) {
        while (!tls->inBuf.empty()) {
            SecBuffer bufs[4] = {
                { (unsigned long)tls->inBuf.size(), SECBUFFER_DATA, tls->inBuf.data() },
                { 0, SECBUFFER_EMPTY, nullptr },
                { 0, SECBUFFER_EMPTY, nullptr },
                { 0, SECBUFFER_EMPTY, nullptr }
            };
            SecBufferDesc desc = { SECBUFFER_VERSION, 4, bufs };
            SECURITY_STATUS sc = DecryptMessage(&tls->ctx, &desc, 0, nullptr);
            if (sc == SEC_E_INCOMPLETE_MESSAGE) {
                tls->needMoreNet = true;
                return true;
            }
            const unsigned char* dataPtr = nullptr;
            size_t dataLen = 0;
            const unsigned char* extraPtr = nullptr;
            size_t extraLen = 0;
            for (int i = 0; i < 4; ++i) {
                if (bufs[i].BufferType == SECBUFFER_DATA && bufs[i].cbBuffer > 0) {
                    dataPtr = (const unsigned char*)bufs[i].pvBuffer;
                    dataLen = bufs[i].cbBuffer;
                } else if (bufs[i].BufferType == SECBUFFER_EXTRA && bufs[i].cbBuffer > 0) {
                    extraPtr = (const unsigned char*)bufs[i].pvBuffer;
                    extraLen = bufs[i].cbBuffer;
                }
            }
            if (sc == SEC_E_OK || sc == SEC_I_RENEGOTIATE || sc == SEC_I_CONTEXT_EXPIRED) {
                if (dataPtr && dataLen > 0) {
                    if (tls->plainOff == tls->plainBuf.size()) {
                        tls->plainBuf.clear();
                        tls->plainOff = 0;
                    }
                    tls->plainBuf.insert(tls->plainBuf.end(), dataPtr, dataPtr + dataLen);
                }
                if (extraPtr && extraLen > 0) {
                    std::memmove(tls->inBuf.data(), extraPtr, extraLen);
                    tls->inBuf.resize(extraLen);
                    tls->needMoreNet = false;
                } else {
                    tls->inBuf.clear();
                    tls->needMoreNet = false;
                }
                if (sc == SEC_I_CONTEXT_EXPIRED) {
                    tls->peerClosed = true;
                    return true;
                }
                if (sc == SEC_I_RENEGOTIATE) {
                    if (!Handshake(tls, false)) return false;
                    continue;
                }
                if (tls->plainBuf.size() > tls->plainOff) return true;
                continue;
            }
            return false;
        }
        return true;
    }

    void CloseSession(TlsSession* tls) {
        if (!tls) return;
        if (tls->hasCtx) DeleteSecurityContext(&tls->ctx);
        if (tls->hasCred) FreeCredentialsHandle(&tls->cred);
        if (tls->sock != INVALID_SOCKET) CloseRaw(tls->sock);
    }

    void HandleClient(SOCKET clientSock) {
        RawOps ops;
        {
            std::lock_guard<std::mutex> lock(mtx_);
            ops = ops_;
        }

        const auto& cfg = Core::Config::Instance();
        SOCKET upstream = ops.dialUpstream ? ops.dialUpstream() : INVALID_SOCKET;
        if (upstream == INVALID_SOCKET) {
            CloseRaw(clientSock);
            return;
        }

        // 握手阶段使用明确收发超时，避免握手卡死
        DWORD recvMs = cfg.timeout.recv_ms > 0 ? (DWORD)cfg.timeout.recv_ms : 5000;
        DWORD sendMs = cfg.timeout.send_ms > 0 ? (DWORD)cfg.timeout.send_ms : 5000;
        setsockopt(upstream, SOL_SOCKET, SO_RCVTIMEO, (const char*)&recvMs, sizeof(recvMs));
        setsockopt(upstream, SOL_SOCKET, SO_SNDTIMEO, (const char*)&sendMs, sizeof(sendMs));

        TlsSession tls;
        tls.sock = upstream;
        tls.insecure = cfg.proxy.tls_insecure;
        tls.sniHost = cfg.proxy.host;

        SCHANNEL_CRED cred{};
        cred.dwVersion = SCHANNEL_CRED_VERSION;
        cred.dwFlags = SCH_CRED_NO_DEFAULT_CREDS | SCH_USE_STRONG_CRYPTO;
        if (tls.insecure) {
            cred.dwFlags |= SCH_CRED_MANUAL_CRED_VALIDATION | SCH_CRED_IGNORE_NO_REVOCATION_CHECK | SCH_CRED_IGNORE_REVOCATION_OFFLINE;
        } else {
            cred.dwFlags |= SCH_CRED_AUTO_CRED_VALIDATION;
        }

        if (AcquireCredentialsHandleA(nullptr, const_cast<SEC_CHAR*>(UNISP_NAME_A), SECPKG_CRED_OUTBOUND,
                                      nullptr, &cred, nullptr, nullptr, &tls.cred, nullptr) != SEC_E_OK) {
            CloseSession(&tls);
            CloseRaw(clientSock);
            return;
        }
        tls.hasCred = true;

        if (!Handshake(&tls, true)) {
            Core::Logger::Error("HTTPS 代理桥: TLS 握手失败, proxy=" + cfg.proxy.host + ":" + std::to_string(cfg.proxy.port));
            CloseSession(&tls);
            CloseRaw(clientSock);
            return;
        }

        // 数据转发阶段取消接收超时
        DWORD zeroTo = 0;
        setsockopt(upstream, SOL_SOCKET, SO_RCVTIMEO, (const char*)&zeroTo, sizeof(zeroTo));
        setsockopt(clientSock, SOL_SOCKET, SO_RCVTIMEO, (const char*)&zeroTo, sizeof(zeroTo));

        std::vector<char> ioBuf(32768);
        for (;;) {
            if (tls.plainBuf.size() > tls.plainOff) {
                size_t avail = tls.plainBuf.size() - tls.plainOff;
                if (!SendRawAll(clientSock, tls.plainBuf.data() + tls.plainOff, (int)avail)) break;
                tls.plainBuf.clear();
                tls.plainOff = 0;
            }
            if (!tls.inBuf.empty() && !tls.needMoreNet) {
                if (!DecryptAvailable(&tls)) break;
                if (tls.plainBuf.size() > tls.plainOff) continue;
            }
            if (tls.peerClosed) break;

            fd_set reads;
            FD_ZERO(&reads);
            FD_SET(clientSock, &reads);
            FD_SET(upstream, &reads);
            timeval tv{ 300, 0 };
            int ready = select(0, &reads, nullptr, nullptr, &tv);
            if (ready <= 0) {
                if (ready < 0) break;
                continue;
            }
            if (FD_ISSET(clientSock, &reads)) {
                int n = RecvRaw(clientSock, ioBuf.data(), (int)ioBuf.size());
                if (n <= 0) break;
                if (!EncryptAndSend(&tls, ioBuf.data(), n)) break;
            }
            if (FD_ISSET(upstream, &reads)) {
                size_t oldSize = tls.inBuf.size();
                tls.inBuf.resize(oldSize + 32768);
                int got = RecvRaw(upstream, (char*)(tls.inBuf.data() + oldSize), 32768);
                if (got <= 0) break;
                tls.inBuf.resize(oldSize + (size_t)got);
                tls.needMoreNet = false;
            }
        }

        CloseSession(&tls);
        CloseRaw(clientSock);
    }

    void AcceptLoop(SOCKET listenSock) {
        while (!stop_.load()) {
            SOCKET clientSock = accept(listenSock, nullptr, nullptr);
            if (clientSock == INVALID_SOCKET) {
                if (stop_.load()) break;
                int err = WSAGetLastError();
                if (err == WSAENOTSOCK || err == WSAEINTR || err == WSAEINVAL) break;
                continue;
            }
            std::thread([this, clientSock]() { HandleClient(clientSock); }).detach();
        }
    }
};

} // namespace Network
