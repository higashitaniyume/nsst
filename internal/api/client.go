package api

import (
	"crypto/tls"
	"net/http"
	"time"

	"github.com/nsst/streamtest/internal/httpx"
)

// clientResponse describes this request as the server sees it. Every field is
// either observed on the connection or read from a request header; nothing is
// resolved against an external service, so the console can show it without the
// page talking to anyone but this server.
type clientResponse struct {
	// IP is the address to display and IPSource names where it came from, so a
	// proxy header is never mistaken for the socket peer.
	IP           string   `json:"ip"`
	IPSource     string   `json:"ip_source"`
	Proxy        bool     `json:"proxy"`
	RemoteAddr   string   `json:"remote_addr"`
	ForwardedFor []string `json:"forwarded_for,omitempty"`

	UserAgent      string `json:"user_agent"`
	AcceptLanguage string `json:"accept_language"`
	Referer        string `json:"referer,omitempty"`
	Host           string `json:"host"`
	Proto          string `json:"proto"`
	TLS            bool   `json:"tls"`
	TLSVersion     string `json:"tls_version,omitempty"`

	// ServerTime is Unix milliseconds, matching the frame field, so the console
	// can report how far the browser clock is from the server clock.
	ServerTime int64 `json:"server_time"`
}

func (a *API) handleClient(w http.ResponseWriter, r *http.Request) {
	ip, source := httpx.ClientIP(r)

	resp := clientResponse{
		IP:             ip,
		IPSource:       source,
		Proxy:          source != httpx.SourceRemoteAddr,
		RemoteAddr:     r.RemoteAddr,
		ForwardedFor:   httpx.ForwardedFor(r),
		UserAgent:      r.UserAgent(),
		AcceptLanguage: r.Header.Get("Accept-Language"),
		Referer:        r.Referer(),
		Host:           r.Host,
		Proto:          r.Proto,
		ServerTime:     time.Now().UnixMilli(),
	}
	if r.TLS != nil {
		resp.TLS = true
		resp.TLSVersion = tlsVersionName(r.TLS.Version)
	}
	httpx.WriteJSON(w, http.StatusOK, resp)
}

func tlsVersionName(version uint16) string {
	switch version {
	case tls.VersionTLS10:
		return "TLS 1.0"
	case tls.VersionTLS11:
		return "TLS 1.1"
	case tls.VersionTLS12:
		return "TLS 1.2"
	case tls.VersionTLS13:
		return "TLS 1.3"
	default:
		return "TLS"
	}
}
