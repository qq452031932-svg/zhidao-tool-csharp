package release

import (
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha1"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

type bundleFixture struct {
	t         *testing.T
	root      string
	files     map[string][]byte
	key       *rsa.PrivateKey
	validator *Validator
	envelope  *upload.Envelope
}

func newBundleFixture(t *testing.T, version string, includeDelta bool) *bundleFixture {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 3072)
	if err != nil {
		t.Fatal(err)
	}
	spki, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	pin := sha256.Sum256(spki)
	trust, err := publisher.NewTrust(spki, hex.EncodeToString(pin[:]))
	if err != nil {
		t.Fatal(err)
	}
	fixture := &bundleFixture{t: t, root: t.TempDir(), files: map[string][]byte{}, key: key, validator: NewValidator(trust)}
	fullName := "SuperPan.BaiduBatchTransfer-" + version + "-full.nupkg"
	fixture.files[fullName] = []byte("full package " + version)
	fixture.files[setupName] = []byte("signed setup placeholder")
	fixture.files[portableName] = []byte("signed portable placeholder")
	assets := []string{fixture.releaseAsset(version, "Full", fullName)}
	catalog := []string{fmt.Sprintf(`{"RelativeFileName":%q,"Type":"Installer"}`, setupName), fmt.Sprintf(`{"RelativeFileName":%q,"Type":"Full"}`, fullName)}
	if includeDelta {
		deltaName := "SuperPan.BaiduBatchTransfer-" + version + "-delta.nupkg"
		fixture.files[deltaName] = []byte("delta package " + version)
		assets = append(assets, fixture.releaseAsset(version, "Delta", deltaName))
		catalog = append(catalog, fmt.Sprintf(`{"RelativeFileName":%q,"Type":"Delta"}`, deltaName))
	}
	catalog = append(catalog, fmt.Sprintf(`{"RelativeFileName":%q,"Type":"Portable"}`, portableName))
	fixture.files[releaseFeedName] = []byte(`{"Assets":[` + strings.Join(assets, ",") + `]}`)
	fixture.files[assetCatalogName] = []byte(`[` + strings.Join(catalog, ",") + `]`)
	fixture.files[releasesIndexName] = []byte(strings.ToUpper(sha1Hex(fixture.files[fullName])) + " " + fullName + fmt.Sprintf(" %d\n", len(fixture.files[fullName])))
	fixture.reseal()
	return fixture
}

func (fixture *bundleFixture) releaseAsset(version, kind, name string) string {
	data := fixture.files[name]
	return fmt.Sprintf(`{"PackageId":"%s","Version":%q,"Type":%q,"FileName":%q,"SHA1":%q,"SHA256":%q,"Size":%d,"NotesMarkdown":"notes","NotesHTML":"<p>notes</p>"}`,
		packageID, version, kind, name, strings.ToUpper(sha1Hex(data)), strings.ToUpper(sha256Hex(data)), len(data))
}

func (fixture *bundleFixture) reseal() {
	fixture.t.Helper()
	delete(fixture.files, manifestName)
	delete(fixture.files, signatureName)
	names := make([]string, 0, len(fixture.files))
	for name := range fixture.files {
		names = append(names, name)
	}
	sort.Strings(names)
	var manifest strings.Builder
	for _, name := range names {
		fmt.Fprintf(&manifest, "%s  %s\n", sha256Hex(fixture.files[name]), name)
	}
	fixture.files[manifestName] = []byte(manifest.String())
	digest := sha256.Sum256(fixture.files[manifestName])
	signature, err := rsa.SignPKCS1v15(rand.Reader, fixture.key, crypto.SHA256, digest[:])
	if err != nil {
		fixture.t.Fatal(err)
	}
	fixture.files[signatureName] = signature
	entries := make([]upload.Entry, 0, len(fixture.files))
	for name, data := range fixture.files {
		path := filepath.Join(fixture.root, name)
		if err := os.WriteFile(path, data, 0o600); err != nil {
			fixture.t.Fatal(err)
		}
		entries = append(entries, upload.Entry{Name: name, Path: path, Size: int64(len(data))})
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Name < entries[j].Name })
	fixture.envelope = &upload.Envelope{ArchivePath: filepath.Join(fixture.root, "bundle.zip"), ExtractionRoot: fixture.root, SHA256: strings.Repeat("a", 64), Entries: entries}
}

func TestValidatorAcceptsExactProductionClosureAndReportsCurrentVersion(t *testing.T) {
	fixture := newBundleFixture(t, "1.2.3", true)
	report, err := fixture.validator.Validate(context.Background(), fixture.envelope, "1.2.2")
	if err != nil {
		t.Fatal(err)
	}
	if !report.Verified || report.PackageID != packageID || report.Channel != channel || report.Version != "1.2.3" || report.FileCount != len(fixture.files) {
		t.Fatalf("report = %#v", report)
	}
	if len(report.Files) != len(fixture.files)-2 || report.PublisherSPKISHA256 == "" {
		t.Fatalf("report files/trust = %#v", report)
	}
}

func TestValidatorRejectsEachFailClosedGate(t *testing.T) {
	tests := []struct {
		name string
		want error
		edit func(*bundleFixture)
	}{
		{"signature", ErrSignature, func(f *bundleFixture) { f.files[signatureName][0] ^= 1 }},
		{"manifest hash", ErrManifest, func(f *bundleFixture) { f.files[setupName] = []byte("tampered") }},
		{"missing manifest member", ErrManifest, func(f *bundleFixture) {
			delete(f.files, portableName)
			f.reseal()
			f.files[manifestName] = append([]byte(nil), f.files[manifestName]...)
			delete(f.files, portableName)
		}},
		{"foreign package", ErrMetadata, func(f *bundleFixture) {
			f.files[releaseFeedName] = []byte(strings.ReplaceAll(string(f.files[releaseFeedName]), packageID, packageID+".OtaAcceptance"))
			f.reseal()
		}},
		{"acceptance channel alias", ErrMetadata, func(f *bundleFixture) {
			f.files[releaseFeedName] = []byte(strings.ReplaceAll(string(f.files[releaseFeedName]), `"Type":"Full"`, `"Type":"Acceptance"`))
			f.reseal()
		}},
		{"release hash", ErrMetadata, func(f *bundleFixture) {
			f.files[releaseFeedName] = []byte(strings.Replace(string(f.files[releaseFeedName]), strings.ToUpper(sha256Hex(f.files["SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg"])), strings.Repeat("0", 64), 1))
			f.reseal()
		}},
		{"catalog reverse closure", ErrCatalog, func(f *bundleFixture) {
			f.files[assetCatalogName] = []byte(`[{"RelativeFileName":"SuperPan.BaiduBatchTransfer-Setup.exe","Type":"Installer"}]`)
			f.reseal()
		}},
		{"releases sha1", ErrReleasesIndex, func(f *bundleFixture) { f.files[releasesIndexName][0] = '0'; f.reseal() }},
		{"unknown json property", ErrMetadata, func(f *bundleFixture) {
			f.files[releaseFeedName] = []byte(strings.Replace(string(f.files[releaseFeedName]), `{"Assets":`, `{"Alias":"bad","Assets":`, 1))
			f.reseal()
		}},
		{"duplicate json property", ErrMetadata, func(f *bundleFixture) {
			f.files[releaseFeedName] = []byte(strings.Replace(string(f.files[releaseFeedName]), `{"Assets":`, `{"Assets":[],"Assets":`, 1))
			f.reseal()
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBundleFixture(t, "1.2.3", true)
			test.edit(fixture)
			if test.name == "signature" || test.name == "manifest hash" {
				fixture.writeCurrentFiles()
			}
			_, err := fixture.validator.Validate(context.Background(), fixture.envelope, "1.2.2")
			if !errorsIs(err, test.want) {
				t.Fatalf("error = %v, want %v", err, test.want)
			}
		})
	}
}

func TestValidatorRequiresVersionStrictlyAbovePermanentFloor(t *testing.T) {
	for _, floor := range []string{"1.2.3", "1.2.4", "65534.0.0"} {
		fixture := newBundleFixture(t, "1.2.3", false)
		if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, floor); !errorsIs(err, ErrVersionFloor) {
			t.Fatalf("floor %q error = %v", floor, err)
		}
	}
}

func (fixture *bundleFixture) writeCurrentFiles() {
	fixture.t.Helper()
	for _, entry := range fixture.envelope.Entries {
		if data, ok := fixture.files[entry.Name]; ok {
			if err := os.WriteFile(entry.Path, data, 0o600); err != nil {
				fixture.t.Fatal(err)
			}
		}
	}
}

func sha256Hex(data []byte) string {
	digest := sha256.Sum256(data)
	return hex.EncodeToString(digest[:])
}
func sha1Hex(data []byte) string { digest := sha1.Sum(data); return hex.EncodeToString(digest[:]) }
func errorsIs(err, target error) bool {
	return err != nil && (err == target || strings.Contains(err.Error(), target.Error()))
}
