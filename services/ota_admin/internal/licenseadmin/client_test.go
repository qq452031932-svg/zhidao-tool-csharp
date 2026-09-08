package licenseadmin

import (
	"bytes"
	"context"
	"encoding/base64"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestNewClientRequiresExplicitLoopbackHTTPOriginAndRawSecret(t *testing.T) {
	t.Parallel()
	secret := make([]byte, 32)
	for _, rawURL := range []string{
		"", "http://localhost:19091", "https://127.0.0.1:19091", "http://0.0.0.0:19091",
		"http://127.0.0.1", "http://127.0.0.1:19091/path", "http://user@127.0.0.1:19091",
	} {
		if _, err := NewClient(Options{BaseURL: rawURL, Secret: secret, MaxRequestBytes: 1024, MaxResponseBytes: 1024}); err == nil {
			t.Fatalf("NewClient accepted %q", rawURL)
		}
	}
	if _, err := NewClient(Options{BaseURL: "http://127.0.0.1:19091", Secret: secret[:31], MaxRequestBytes: 1024, MaxResponseBytes: 1024}); err == nil {
		t.Fatal("NewClient accepted a non-32-byte secret")
	}
}

func TestTypedClientUsesCanonicalBearerFixedPathActorAndIdempotency(t *testing.T) {
	t.Parallel()
	secret := make([]byte, 32)
	for i := range secret {
		secret[i] = byte(i)
	}
	const key = "1b385465-f173-49c7-b19e-10fb8e38bd9d"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost || r.URL.RequestURI() != "/internal/v1/software" {
			t.Fatalf("request = %s %s", r.Method, r.URL.RequestURI())
		}
		wantBearer := "Bearer " + base64.RawURLEncoding.EncodeToString(secret)
		if got := r.Header.Get("Authorization"); got != wantBearer {
			t.Fatalf("Authorization = %q, want canonical wire token", got)
		}
		if got := r.Header.Get("Idempotency-Key"); got != key {
			t.Fatalf("Idempotency-Key = %q", got)
		}
		if got := r.Header.Get("X-Admin-Actor"); got != "session-principal-hash" {
			t.Fatalf("X-Admin-Actor = %q", got)
		}
		body, _ := io.ReadAll(r.Body)
		if string(body) != `{"name":"SuperPan"}` {
			t.Fatalf("body = %q", body)
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusCreated)
		_, _ = w.Write([]byte(`{"id":"ok"}`))
	}))
	defer server.Close()

	client := newTestClient(t, server.URL, secret, 1024, 1024)
	result, err := client.CreateSoftware(context.Background(), []byte(`{"name":"SuperPan"}`), Mutation{Actor: "session-principal-hash", IdempotencyKey: key})
	if err != nil || result.StatusCode != http.StatusCreated || string(result.Body) != `{"id":"ok"}` {
		t.Fatalf("CreateSoftware = %#v, %v", result, err)
	}
	if strings.Contains(string(result.Body), base64.RawURLEncoding.EncodeToString(secret)) {
		t.Fatal("response exposed the internal bearer")
	}
}

func TestTypedClientRejectsInjectedIDsAndBoundsBothDirections(t *testing.T) {
	t.Parallel()
	secret := make([]byte, 32)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(strings.Repeat("x", 65)))
	}))
	defer server.Close()
	client := newTestClient(t, server.URL, secret, 32, 64)
	mutation := Mutation{Actor: "actor", IdempotencyKey: "1b385465-f173-49c7-b19e-10fb8e38bd9d"}
	if _, err := client.RenameSoftware(context.Background(), "../cards", []byte(`{"name":"x"}`), mutation); err == nil {
		t.Fatal("RenameSoftware accepted an injected resource id")
	}
	if _, err := client.CreateSoftware(context.Background(), []byte(strings.Repeat("x", 33)), mutation); err == nil {
		t.Fatal("CreateSoftware accepted an oversized request")
	}
	if _, err := client.ListSoftware(context.Background(), Page{Number: 1, Size: 20}); err == nil {
		t.Fatal("ListSoftware accepted an oversized response")
	}
}

func TestTypedClientReturnsUnavailableWithoutLeakingTransportDetails(t *testing.T) {
	t.Parallel()
	client := newTestClient(t, "http://127.0.0.1:1", make([]byte, 32), 1024, 1024)
	_, err := client.ListPackages(context.Background(), Page{Number: 1, Size: 20})
	if !IsUnavailable(err) {
		t.Fatalf("ListPackages error = %v", err)
	}
	if strings.Contains(strings.ToLower(err.Error()), "connect") || strings.Contains(err.Error(), "127.0.0.1") {
		t.Fatalf("transport details leaked: %v", err)
	}
}

func TestTypedClientDoesNotFollowRedirectsOrReturnBearerMaterial(t *testing.T) {
	t.Parallel()
	secret := bytes.Repeat([]byte{0x73}, 32)
	var redirected bool
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		redirected = true
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer target.Close()
	redirector := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, target.URL, http.StatusTemporaryRedirect)
	}))
	defer redirector.Close()
	client := newTestClient(t, redirector.URL, secret, 1024, 1024)
	if _, err := client.ListSoftware(context.Background(), Page{Number: 1, Size: 20}); !IsUnavailable(err) {
		t.Fatalf("redirect result error = %v", err)
	}
	if redirected {
		t.Fatal("client followed a redirect from the fixed loopback origin")
	}

	wireToken := base64.RawURLEncoding.EncodeToString(secret)
	leaker := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"debug":"` + wireToken + `"}`))
	}))
	defer leaker.Close()
	client = newTestClient(t, leaker.URL, secret, 1024, 1024)
	if _, err := client.ListSoftware(context.Background(), Page{Number: 1, Size: 20}); !IsUnavailable(err) {
		t.Fatalf("bearer leak result error = %v", err)
	}
}

func TestCardSearchQueryIsBounded(t *testing.T) {
	t.Parallel()
	client := newTestClient(t, "http://127.0.0.1:1", make([]byte, 32), 1024, 1024)
	_, err := client.SearchCards(context.Background(), CardSearch{Page: Page{Number: 1, Size: 20}, NoteContains: strings.Repeat("x", 5000)})
	if err == nil || IsUnavailable(err) {
		t.Fatalf("oversized search error = %v", err)
	}
}

func TestTypedClientRejectsMalformedDownstreamContentType(t *testing.T) {
	t.Parallel()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json-evil")
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer server.Close()
	client := newTestClient(t, server.URL, make([]byte, 32), 1024, 1024)
	if _, err := client.ListDevices(context.Background(), Page{Number: 1, Size: 20}); !IsUnavailable(err) {
		t.Fatalf("malformed content type error = %v", err)
	}
}

func TestTypedClientCanonicalizesForwardedResponseHeaders(t *testing.T) {
	t.Parallel()
	secret := bytes.Repeat([]byte{0x29}, 32)
	token := base64.RawURLEncoding.EncodeToString(secret)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", `application/json; internal="`+token+`"`)
		w.Header().Set("Content-Disposition", `attachment; filename="`+token+`"`)
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer server.Close()
	client := newTestClient(t, server.URL, secret, 1024, 1024)
	result, err := client.ListSoftware(context.Background(), Page{Number: 1, Size: 20})
	if err != nil {
		t.Fatal(err)
	}
	if result.ContentType != "application/json" || result.ContentDisposition != "" {
		t.Fatalf("unsafe response headers survived: %#v", result)
	}
}

func newTestClient(t *testing.T, baseURL string, secret []byte, maxRequest, maxResponse int64) *Client {
	t.Helper()
	client, err := NewClient(Options{BaseURL: baseURL, Secret: secret, HTTPClient: http.DefaultClient, MaxRequestBytes: maxRequest, MaxResponseBytes: maxResponse})
	if err != nil {
		t.Fatal(err)
	}
	return client
}
