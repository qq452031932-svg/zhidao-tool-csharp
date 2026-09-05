package security

import (
	"net"
	"net/http"
	"net/url"
	"strings"
)

func SameOriginRequest(request *http.Request, expected string) bool {
	want, ok := canonicalOrigin(expected)
	if !ok {
		return false
	}
	origin, originOK := optionalSingleHeader(request, "Origin")
	referer, refererOK := optionalSingleHeader(request, "Referer")
	if !originOK || !refererOK {
		return false
	}
	if origin == "" && referer == "" {
		return false
	}
	if origin != "" {
		got, valid := canonicalOrigin(origin)
		if !valid || got != want {
			return false
		}
	}
	if referer != "" {
		parsed, err := url.Parse(referer)
		if err != nil || parsed.User != nil || parsed.Scheme == "" || parsed.Host == "" {
			return false
		}
		got, valid := canonicalOrigin(parsed.Scheme + "://" + parsed.Host)
		if !valid || got != want {
			return false
		}
	}
	return true
}

func canonicalOrigin(value string) (string, bool) {
	parsed, err := url.Parse(value)
	if err != nil || parsed.User != nil || parsed.Scheme == "" || parsed.Host == "" || parsed.Path != "" || parsed.RawQuery != "" || parsed.Fragment != "" {
		return "", false
	}
	scheme := strings.ToLower(parsed.Scheme)
	if scheme != "https" && scheme != "http" {
		return "", false
	}
	host := strings.ToLower(parsed.Hostname())
	if host == "" {
		return "", false
	}
	port := parsed.Port()
	if (scheme == "https" && port == "443") || (scheme == "http" && port == "80") {
		port = ""
	}
	if port != "" {
		host = net.JoinHostPort(host, port)
	}
	return scheme + "://" + host, true
}

func TrustedSourceAddress(request *http.Request) string {
	peer := parseRemoteIP(request.RemoteAddr)
	if peer == nil {
		return "unknown"
	}
	if peer.IsLoopback() {
		forwardedValue, forwardedOK := optionalSingleHeader(request, "X-Forwarded-For")
		if forwardedOK && forwardedValue != "" {
			if forwarded := singleForwardedIP(forwardedValue); forwarded != nil {
				return forwarded.String()
			}
		}
		realValue, realOK := optionalSingleHeader(request, "X-Real-IP")
		if forwardedValue == "" && forwardedOK && realOK {
			if realIP := net.ParseIP(realValue); realIP != nil {
				return realIP.String()
			}
		}
	}
	return peer.String()
}

func optionalSingleHeader(request *http.Request, name string) (string, bool) {
	values := request.Header.Values(name)
	if len(values) > 1 {
		return "", false
	}
	if len(values) == 0 {
		return "", true
	}
	return strings.TrimSpace(values[0]), true
}

func parseRemoteIP(value string) net.IP {
	host, _, err := net.SplitHostPort(value)
	if err != nil {
		host = value
	}
	return net.ParseIP(strings.Trim(host, "[]"))
}

func singleForwardedIP(value string) net.IP {
	value = strings.TrimSpace(value)
	if value == "" || strings.Contains(value, ",") {
		return nil
	}
	return net.ParseIP(value)
}
