package httpx

import (
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"
)

func TestClientIPPrefersProxyHeaders(t *testing.T) {
	tests := []struct {
		name       string
		remoteAddr string
		headers    map[string]string
		wantIP     string
		wantSource string
	}{
		{
			name:       "remote address when nothing else is set",
			remoteAddr: "198.51.100.9:44321",
			wantIP:     "198.51.100.9",
			wantSource: SourceRemoteAddr,
		},
		{
			name:       "a bare address without a port",
			remoteAddr: "198.51.100.9",
			wantIP:     "198.51.100.9",
			wantSource: SourceRemoteAddr,
		},
		{
			name:       "x-forwarded-for wins over the socket peer",
			remoteAddr: "10.0.0.1:5000",
			headers:    map[string]string{"X-Forwarded-For": "203.0.113.7, 10.0.0.1"},
			wantIP:     "203.0.113.7",
			wantSource: SourceXForwardedFor,
		},
		{
			name:       "cf-connecting-ip wins over x-forwarded-for",
			remoteAddr: "10.0.0.1:5000",
			headers: map[string]string{
				"CF-Connecting-IP": "203.0.113.7",
				"X-Forwarded-For":  "198.51.100.4",
			},
			wantIP:     "203.0.113.7",
			wantSource: SourceCFConnectingIP,
		},
		{
			name:       "x-real-ip is used when nothing better exists",
			remoteAddr: "10.0.0.1:5000",
			headers:    map[string]string{"X-Real-IP": "203.0.113.8"},
			wantIP:     "203.0.113.8",
			wantSource: SourceXRealIP,
		},
		{
			name:       "an unparsable header falls through to the socket peer",
			remoteAddr: "10.0.0.1:5000",
			headers:    map[string]string{"X-Real-IP": "not-an-address", "X-Forwarded-For": ""},
			wantIP:     "10.0.0.1",
			wantSource: SourceRemoteAddr,
		},
		{
			name:       "a port on a proxy header is stripped",
			remoteAddr: "10.0.0.1:5000",
			headers:    map[string]string{"X-Forwarded-For": "203.0.113.7:61337"},
			wantIP:     "203.0.113.7",
			wantSource: SourceXForwardedFor,
		},
		{
			name:       "an ipv6 literal keeps its canonical form",
			remoteAddr: "[2001:db8::1]:5000",
			wantIP:     "2001:db8::1",
			wantSource: SourceRemoteAddr,
		},
		{
			name:       "a bracketed ipv6 proxy header is unwrapped",
			remoteAddr: "10.0.0.1:5000",
			headers:    map[string]string{"X-Real-IP": "[2001:db8::2]:443"},
			wantIP:     "2001:db8::2",
			wantSource: SourceXRealIP,
		},
		{
			name:       "an ipv4-mapped ipv6 address is folded",
			remoteAddr: "10.0.0.1:5000",
			headers:    map[string]string{"X-Forwarded-For": "::ffff:203.0.113.9"},
			wantIP:     "203.0.113.9",
			wantSource: SourceXForwardedFor,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			r := httptest.NewRequest(http.MethodGet, "/", nil)
			r.RemoteAddr = tc.remoteAddr
			for name, value := range tc.headers {
				r.Header.Set(name, value)
			}

			ip, source := ClientIP(r)
			if ip != tc.wantIP {
				t.Errorf("ip = %q, want %q", ip, tc.wantIP)
			}
			if source != tc.wantSource {
				t.Errorf("source = %q, want %q", source, tc.wantSource)
			}
		})
	}
}

func TestForwardedForChain(t *testing.T) {
	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.Header.Set("X-Forwarded-For", "203.0.113.7, 198.51.100.4:1234, garbage, [2001:db8::3]")

	want := []string{"203.0.113.7", "198.51.100.4", "2001:db8::3"}
	if got := ForwardedFor(r); !reflect.DeepEqual(got, want) {
		t.Errorf("ForwardedFor = %v, want %v", got, want)
	}
}

func TestForwardedForEmpty(t *testing.T) {
	r := httptest.NewRequest(http.MethodGet, "/", nil)
	if got := ForwardedFor(r); got != nil {
		t.Errorf("ForwardedFor = %v, want nil", got)
	}
}

func TestNormalizeIP(t *testing.T) {
	tests := map[string]string{
		"":                  "",
		"   ":               "",
		"203.0.113.7":       "203.0.113.7",
		" 203.0.113.7 ":     "203.0.113.7",
		"203.0.113.7:8080":  "203.0.113.7",
		"[2001:db8::1]":     "2001:db8::1",
		"[2001:db8::1]:443": "2001:db8::1",
		"2001:db8::1":       "2001:db8::1",
		"::ffff:10.0.0.1":   "10.0.0.1",
		"localhost":         "",
		"999.1.1.1":         "",
	}

	for input, want := range tests {
		if got := normalizeIP(input); got != want {
			t.Errorf("normalizeIP(%q) = %q, want %q", input, got, want)
		}
	}
}
