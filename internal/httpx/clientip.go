package httpx

import (
	"net"
	"net/http"
	"strings"
)

// Proxy headers that may carry the original client address, consulted in the
// order below. The single-address headers are set by one hop directly in front
// of this server; X-Forwarded-For is a list that every hop appends to, so its
// left-most entry is the original client.
const (
	headerCFConnectingIP = "CF-Connecting-IP"
	headerTrueClientIP   = "True-Client-IP"
	headerXRealIP        = "X-Real-IP"
	headerXForwardedFor  = "X-Forwarded-For"
)

// Values reported in the ip_source field, so a caller can tell an address the
// proxy asserted apart from the socket peer.
const (
	SourceCFConnectingIP = "cf-connecting-ip"
	SourceTrueClientIP   = "true-client-ip"
	SourceXRealIP        = "x-real-ip"
	SourceXForwardedFor  = "x-forwarded-for"
	SourceRemoteAddr     = "remote-addr"
)

// ClientIP returns the client address to display and the name of the header it
// came from, falling back to SourceRemoteAddr when no proxy header is usable.
//
// A proxy header is exactly as trustworthy as the proxy that sets it: with no
// proxy in front of the server these values are trivially spoofable, which is
// why the source travels with the address instead of being hidden. Candidates
// are normalized to a bare IP (no port, no brackets) and anything unparsable is
// skipped rather than displayed.
func ClientIP(r *http.Request) (string, string) {
	for _, candidate := range []struct{ header, source string }{
		{headerCFConnectingIP, SourceCFConnectingIP},
		{headerTrueClientIP, SourceTrueClientIP},
		{headerXRealIP, SourceXRealIP},
	} {
		if ip := normalizeIP(r.Header.Get(candidate.header)); ip != "" {
			return ip, candidate.source
		}
	}
	if chain := ForwardedFor(r); len(chain) > 0 {
		return chain[0], SourceXForwardedFor
	}
	if ip := normalizeIP(r.RemoteAddr); ip != "" {
		return ip, SourceRemoteAddr
	}
	// The peer address was unparsable; report it verbatim rather than nothing.
	return r.RemoteAddr, SourceRemoteAddr
}

// ForwardedFor returns the X-Forwarded-For chain with every entry normalized to
// a bare IP, oldest hop first. Unparsable entries are dropped.
func ForwardedFor(r *http.Request) []string {
	raw := r.Header.Get(headerXForwardedFor)
	if raw == "" {
		return nil
	}
	var chain []string
	for _, part := range strings.Split(raw, ",") {
		if ip := normalizeIP(part); ip != "" {
			chain = append(chain, ip)
		}
	}
	return chain
}

// normalizeIP accepts "1.2.3.4", "1.2.3.4:5678", "[::1]", "[::1]:5678" and a
// bare IPv6 literal, and returns the canonical address, or "" when the input is
// not an address at all.
func normalizeIP(raw string) string {
	value := strings.TrimSpace(raw)
	if value == "" {
		return ""
	}

	switch {
	case strings.HasPrefix(value, "["):
		// Either "[::1]:5678" or a bare "[::1]".
		if host, _, err := net.SplitHostPort(value); err == nil {
			value = host
		} else if end := strings.IndexByte(value, ']'); end > 0 {
			value = value[1:end]
		}
	default:
		// A host without a port makes SplitHostPort fail, which is the common
		// case for a proxy header and must not be treated as an error.
		if host, _, err := net.SplitHostPort(value); err == nil {
			value = host
		}
	}

	value = strings.TrimSpace(value)
	if value == "" {
		return ""
	}
	ip := net.ParseIP(value)
	if ip == nil {
		return ""
	}
	// ip.String() canonicalizes, folding "::ffff:1.2.3.4" into "1.2.3.4".
	return ip.String()
}
