package release

import (
	"archive/zip"
	"bytes"
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha1"
	"crypto/sha256"
	"crypto/x509"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

type fixtureAsset struct {
	version    string
	kind       string
	layout     string
	baseLayout string
}

const (
	fixtureLayoutLegacy  = "legacy"
	fixtureLayoutRenamed = "renamed"
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
	assets := []fixtureAsset{{version: version, kind: "Full", layout: fixtureLayoutRenamed}}
	if includeDelta {
		assets = append([]fixtureAsset{{version: previousVersion(t, version), kind: "Full", layout: fixtureLayoutLegacy}}, assets...)
		assets = append(assets, fixtureAsset{version: version, kind: "Delta", layout: fixtureLayoutRenamed, baseLayout: fixtureLayoutLegacy})
	}
	return newBundleFixtureWithAssets(t, assets)
}

func newBundleFixtureWithAssets(t *testing.T, specs []fixtureAsset) *bundleFixture {
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
	currentVersion := specs[0].version
	parsedCurrent, err := parseVersion(currentVersion)
	if err != nil {
		t.Fatal(err)
	}
	assets := make([]string, 0, len(specs))
	fullNames := make([]string, 0, len(specs))
	hasCurrentDelta := false
	for _, spec := range specs {
		parsed, parseErr := parseVersion(spec.version)
		if parseErr != nil {
			t.Fatal(parseErr)
		}
		if parsed.compare(parsedCurrent) > 0 {
			currentVersion, parsedCurrent = spec.version, parsed
		}
	}
	for _, spec := range specs {
		name := fmt.Sprintf("%s-%s-%s.nupkg", packageID, spec.version, strings.ToLower(spec.kind))
		fixture.files[name] = makeNupkg(t, spec.version, spec.kind, channel, spec.layout, spec.baseLayout)
		assets = append(assets, fixture.releaseAsset(spec.version, spec.kind, name))
		if spec.kind == "Full" {
			fullNames = append(fullNames, name)
		}
	}
	for _, spec := range specs {
		if spec.version == currentVersion && spec.kind == "Delta" {
			hasCurrentDelta = true
		}
	}
	fixture.files[setupName] = makeSetupImage()
	fixture.files[portableName] = makePortable(t, currentVersion, channel)
	currentFullName := fmt.Sprintf("%s-%s-full.nupkg", packageID, currentVersion)
	catalog := []string{fmt.Sprintf(`{"RelativeFileName":%q,"Type":"Installer"}`, setupName), fmt.Sprintf(`{"RelativeFileName":%q,"Type":"Full"}`, currentFullName)}
	if hasCurrentDelta {
		catalog = append(catalog, fmt.Sprintf(`{"RelativeFileName":%q,"Type":"Delta"}`, fmt.Sprintf("%s-%s-delta.nupkg", packageID, currentVersion)))
	}
	catalog = append(catalog, fmt.Sprintf(`{"RelativeFileName":%q,"Type":"Portable"}`, portableName))
	fixture.files[releaseFeedName] = []byte(`{"Assets":[` + strings.Join(assets, ",") + `]}`)
	fixture.files[assetCatalogName] = []byte(`[` + strings.Join(catalog, ",") + `]`)
	sort.Slice(fullNames, func(i, j int) bool {
		left := packageFile.FindStringSubmatch(fullNames[i])[1]
		right := packageFile.FindStringSubmatch(fullNames[j])[1]
		leftVersion, _ := parseVersion(left)
		rightVersion, _ := parseVersion(right)
		return leftVersion.compare(rightVersion) < 0
	})
	var releases strings.Builder
	for _, name := range fullNames {
		fmt.Fprintf(&releases, "%s %s %d\n", strings.ToUpper(sha1Hex(fixture.files[name])), name, len(fixture.files[name]))
	}
	fixture.files[releasesIndexName] = []byte(releases.String())
	fixture.reseal()
	return fixture
}

func previousVersion(t *testing.T, value string) string {
	t.Helper()
	parts := strings.Split(value, ".")
	numbers := make([]int, 3)
	for index := range numbers {
		number, err := strconv.Atoi(parts[index])
		if err != nil {
			t.Fatal(err)
		}
		numbers[index] = number
	}
	if numbers[2] > 0 {
		numbers[2]--
	} else if numbers[1] > 0 {
		numbers[1]--
	} else if numbers[0] > 0 {
		numbers[0]--
	} else {
		t.Fatal("fixture version has no lower predecessor")
	}
	return fmt.Sprintf("%d.%d.%d", numbers[0], numbers[1], numbers[2])
}

func makeNupkg(t *testing.T, version, kind, packageChannel, layout, baseLayout string) []byte {
	t.Helper()
	mainExecutable := productLegacyExecutableName
	if layout == fixtureLayoutRenamed {
		mainExecutable = productMainExecutableName
	}
	entries := map[string][]byte{
		"[Content_Types].xml": []byte(`<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>`),
		"_rels/.rels":         []byte(`<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>`),
		packageID + ".nuspec": []byte(packageMetadata(version, packageChannel, mainExecutable)),
	}
	if kind == "Full" {
		assemblyName := productLegacyAssemblyName
		dllName := assemblyName + ".dll"
		if layout == fixtureLayoutRenamed {
			assemblyName = productMainAssemblyName
			dllName = productMainDLLName
		}
		entries["lib/app/"+dllName] = []byte("managed assembly")
		entries["lib/app/"+assemblyName+".deps.json"] = []byte(`{}`)
		entries["lib/app/"+assemblyName+".runtimeconfig.json"] = []byte(`{}`)
		entries["lib/app/"+assemblyName+"_ExecutionStub.exe"] = makeSetupImage()
		entries["lib/app/"+mainExecutable] = makeSetupImage()
		if layout == fixtureLayoutRenamed {
			entries["lib/app/"+productLegacyExecutableName] = makeSetupImage()
		}
		entries["lib/app/Squirrel.exe"] = makeSetupImage()
	} else {
		if baseLayout == fixtureLayoutLegacy {
			entries["lib/app/"+productLegacyExecutableName+".diff"] = []byte("real patch bytes")
			entries["lib/app/"+productLegacyExecutableName+".shasum"] = []byte(strings.Repeat("0", 64))
			entries["lib/app/"+productMainAssemblyName+"_ExecutionStub.exe"] = makeSetupImage()
			entries["lib/app/"+productMainExecutableName] = makeSetupImage()
			entries["lib/app/"+productMainDLLName] = []byte("managed assembly")
			entries["lib/app/"+productMainAssemblyName+".deps.json"] = []byte(`{}`)
			entries["lib/app/"+productMainAssemblyName+".runtimeconfig.json"] = []byte(`{}`)
		} else {
			entries["lib/app/"+productMainExecutableName+".diff"] = []byte("routine patch bytes")
			entries["lib/app/"+productMainExecutableName+".shasum"] = []byte(strings.Repeat("0", 64))
		}
	}
	return zipBytes(t, entries)
}

func makePortable(t *testing.T, version, packageChannel string) []byte {
	t.Helper()
	return zipBytes(t, map[string][]byte{
		".portable":                     {},
		"current/sq.version":            []byte(packageMetadata(version, packageChannel, productMainExecutableName)),
		"current/" + productMainDLLName: []byte("managed assembly"),
		"current/" + productMainAssemblyName + ".deps.json":          []byte(`{}`),
		"current/" + productMainAssemblyName + ".runtimeconfig.json": []byte(`{}`),
		productMainExecutableName:                                    makeSetupImage(),
		"Update.exe":                                                 makeSetupImage(),
		"current/" + productMainExecutableName:                       makeSetupImage(),
		"current/" + productLegacyExecutableName:                     makeSetupImage(),
	})
}

func packageMetadata(version, packageChannel, mainExecutable string) string {
	return fmt.Sprintf(`<?xml version="1.0" encoding="utf-8"?><package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd"><metadata><id>%s</id><version>%s</version><channel>%s</channel><mainExe>%s</mainExe><os>win</os><rid>win-x64</rid><machineArchitecture>x64</machineArchitecture></metadata></package>`, packageID, version, packageChannel, mainExecutable)
}

func zipBytes(t *testing.T, entries map[string][]byte) []byte {
	t.Helper()
	var output bytes.Buffer
	archive := zip.NewWriter(&output)
	names := make([]string, 0, len(entries))
	for name := range entries {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		entry, err := archive.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := entry.Write(entries[name]); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return output.Bytes()
}

func readZipBytes(t *testing.T, data []byte) map[string][]byte {
	t.Helper()
	reader, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		t.Fatal(err)
	}
	entries := make(map[string][]byte, len(reader.File))
	for _, archived := range reader.File {
		stream, err := archived.Open()
		if err != nil {
			t.Fatal(err)
		}
		content, readErr := io.ReadAll(stream)
		closeErr := stream.Close()
		if readErr != nil || closeErr != nil {
			t.Fatalf("read %s: read=%v close=%v", archived.Name, readErr, closeErr)
		}
		entries[archived.Name] = content
	}
	return entries
}

func zipBytesWithReparseEntry(t *testing.T, entries map[string][]byte, reparseName string) []byte {
	t.Helper()
	var output bytes.Buffer
	archive := zip.NewWriter(&output)
	names := make([]string, 0, len(entries))
	for name := range entries {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		header := &zip.FileHeader{Name: name, Method: zip.Deflate}
		if name == reparseName {
			header.CreatorVersion = 0
			header.ExternalAttrs = 0x400
		}
		entry, err := archive.CreateHeader(header)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := entry.Write(entries[name]); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return output.Bytes()
}

func makeSetupImage() []byte {
	image := make([]byte, 512)
	copy(image, []byte{'M', 'Z'})
	binary.LittleEndian.PutUint32(image[0x3c:], 0x80)
	copy(image[0x80:], []byte{'P', 'E', 0, 0})
	binary.LittleEndian.PutUint16(image[0x84:], 0x8664)
	return image
}

func (fixture *bundleFixture) releaseAsset(version, kind, name string) string {
	data := fixture.files[name]
	return fmt.Sprintf(`{"PackageId":"%s","Version":%q,"Type":%q,"FileName":%q,"SHA1":%q,"SHA256":%q,"Size":%d,"NotesMarkdown":"notes","NotesHTML":"<p>notes</p>"}`,
		packageID, version, kind, name, strings.ToUpper(sha1Hex(data)), strings.ToUpper(sha256Hex(data)), len(data))
}

func TestValidationReportIncludesSignedCurrentReleaseNotes(t *testing.T) {
	fixture := newBundleFixture(t, "1.2.3", false)
	report, err := fixture.validator.Validate(context.Background(), fixture.envelope, "")
	if err != nil {
		t.Fatal(err)
	}
	if report.NotesMarkdown != "notes" {
		t.Fatalf("NotesMarkdown=%q, want signed current release notes", report.NotesMarkdown)
	}
}

func TestValidationRejectsReleaseNotesThatCannotBePersisted(t *testing.T) {
	for _, test := range []struct {
		name    string
		size    int
		wantErr bool
	}{{name: "exact limit", size: MaximumNotesBytes}, {name: "one byte too large", size: MaximumNotesBytes + 1, wantErr: true}} {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBundleFixture(t, "1.2.3", false)
			notes := strings.Repeat("n", test.size)
			fixture.files[releaseFeedName] = bytes.ReplaceAll(fixture.files[releaseFeedName], []byte(`"NotesMarkdown":"notes"`), []byte(`"NotesMarkdown":`+strconv.Quote(notes)))
			fixture.reseal()
			fixture.writeCurrentFiles()
			_, err := fixture.validator.Validate(context.Background(), fixture.envelope, "")
			if errorsIs(err, ErrMetadata) != test.wantErr {
				t.Fatalf("size=%d error=%v", test.size, err)
			}
		})
	}
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

func TestValidatorAcceptsRenamedHistoricalFullAndRoutineCurrentDelta(t *testing.T) {
	fixture := newBundleFixtureWithAssets(t, []fixtureAsset{
		{version: "1.2.2", kind: "Full", layout: fixtureLayoutLegacy},
		{version: "1.2.3", kind: "Full", layout: fixtureLayoutRenamed},
		{version: "1.2.4", kind: "Full", layout: fixtureLayoutRenamed},
		{version: "1.2.4", kind: "Delta", layout: fixtureLayoutRenamed, baseLayout: fixtureLayoutRenamed},
	})
	report, err := fixture.validator.Validate(context.Background(), fixture.envelope, "1.2.0")
	if err != nil {
		t.Fatal(err)
	}
	if report.Version != "1.2.4" {
		t.Fatalf("version = %q", report.Version)
	}
}

func TestValidatorRejectsDeltaThatDoesNotMatchHighestLowerFullLayout(t *testing.T) {
	fixture := newBundleFixtureWithAssets(t, []fixtureAsset{
		{version: "1.2.2", kind: "Full", layout: fixtureLayoutLegacy},
		{version: "1.2.3", kind: "Full", layout: fixtureLayoutRenamed},
		{version: "1.2.4", kind: "Full", layout: fixtureLayoutRenamed},
		{version: "1.2.4", kind: "Delta", layout: fixtureLayoutRenamed, baseLayout: fixtureLayoutLegacy},
	})
	if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
		t.Fatalf("error = %v, want %v", err, ErrMetadata)
	}
}

func TestValidatorRejectsEmptyOptionalPublisherExecutableInRoutineDelta(t *testing.T) {
	fixture := newBundleFixtureWithAssets(t, []fixtureAsset{
		{version: "1.2.3", kind: "Full", layout: fixtureLayoutRenamed},
		{version: "1.2.4", kind: "Full", layout: fixtureLayoutRenamed},
		{version: "1.2.4", kind: "Delta", layout: fixtureLayoutRenamed, baseLayout: fixtureLayoutRenamed},
	})
	name := fmt.Sprintf("%s-1.2.4-delta.nupkg", packageID)
	entries := readZipBytes(t, fixture.files[name])
	entries["lib/app/"+productLegacyExecutableName] = []byte{}
	fixture.replaceReleaseAsset(name, zipBytes(t, entries))
	if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
		t.Fatalf("error = %v, want %v", err, ErrMetadata)
	}
}

func TestValidatorRejectsHistoricalDeltaEvenWhenCurrentDeltaExists(t *testing.T) {
	fixture := newBundleFixtureWithAssets(t, []fixtureAsset{
		{version: "1.2.1", kind: "Full", layout: fixtureLayoutLegacy},
		{version: "1.2.2", kind: "Full", layout: fixtureLayoutLegacy},
		{version: "1.2.2", kind: "Delta", layout: fixtureLayoutLegacy, baseLayout: fixtureLayoutLegacy},
		{version: "1.2.3", kind: "Full", layout: fixtureLayoutRenamed},
		{version: "1.2.3", kind: "Delta", layout: fixtureLayoutRenamed, baseLayout: fixtureLayoutLegacy},
	})
	if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
		t.Fatalf("error = %v, want %v", err, ErrMetadata)
	}
}

func TestValidatorRejectsDeltaWithoutLowerFullAndMissingCurrentDelta(t *testing.T) {
	tests := []struct {
		name   string
		assets []fixtureAsset
	}{
		{"no lower Full base", []fixtureAsset{{version: "1.2.3", kind: "Full", layout: fixtureLayoutRenamed}, {version: "1.2.3", kind: "Delta", layout: fixtureLayoutRenamed, baseLayout: fixtureLayoutLegacy}}},
		{"historical Delta but no current Delta", []fixtureAsset{
			{version: "1.2.1", kind: "Full", layout: fixtureLayoutLegacy},
			{version: "1.2.2", kind: "Full", layout: fixtureLayoutRenamed},
			{version: "1.2.2", kind: "Delta", layout: fixtureLayoutRenamed, baseLayout: fixtureLayoutLegacy},
			{version: "1.2.3", kind: "Full", layout: fixtureLayoutRenamed},
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBundleFixtureWithAssets(t, test.assets)
			if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
				t.Fatalf("error = %v, want %v", err, ErrMetadata)
			}
		})
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
		{"case folded duplicate json property", ErrMetadata, func(f *bundleFixture) {
			f.files[releaseFeedName] = []byte(strings.Replace(string(f.files[releaseFeedName]), `{"Assets":`, `{"assets":[],"Assets":`, 1))
			f.reseal()
		}},
		{"wrong case root json property", ErrMetadata, func(f *bundleFixture) {
			f.files[releaseFeedName] = []byte(strings.Replace(string(f.files[releaseFeedName]), `{"Assets":`, `{"assets":`, 1))
			f.reseal()
		}},
		{"wrong case nested json property", ErrMetadata, func(f *bundleFixture) {
			f.files[releaseFeedName] = []byte(strings.Replace(string(f.files[releaseFeedName]), `"PackageId":`, `"packageid":`, 1))
			f.reseal()
		}},
		{"wrong case catalog json property", ErrCatalog, func(f *bundleFixture) {
			f.files[assetCatalogName] = []byte(strings.Replace(string(f.files[assetCatalogName]), `"RelativeFileName":`, `"relativefilename":`, 1))
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

func TestValidatorChecksCatalogAndReleasesBeforePermanentFloor(t *testing.T) {
	fixture := newBundleFixture(t, "1.2.3", false)
	fixture.files[assetCatalogName] = []byte(`[]`)
	fixture.reseal()
	if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, "1.2.3"); !errorsIs(err, ErrCatalog) {
		t.Fatalf("error = %v, want %v", err, ErrCatalog)
	}

	fixture = newBundleFixture(t, "1.2.3", false)
	fixture.files[releasesIndexName][0] = '0'
	fixture.reseal()
	if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, "1.2.3"); !errorsIs(err, ErrReleasesIndex) {
		t.Fatalf("error = %v, want %v", err, ErrReleasesIndex)
	}
}

func TestValidatorRejectsArbitraryOrMismatchedPackageStructures(t *testing.T) {
	tests := []struct {
		name string
		edit func(*bundleFixture)
	}{
		{"arbitrary Full blob", func(f *bundleFixture) {
			f.replaceReleaseAsset("SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg", []byte("not a package"))
		}},
		{"renamed Acceptance Full", func(f *bundleFixture) {
			f.replaceReleaseAsset("SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg", makeNupkg(t, "1.2.3", "Full", "ota-acceptance", fixtureLayoutRenamed, ""))
		}},
		{"Delta without patch structure", func(f *bundleFixture) {
			name := "SuperPan.BaiduBatchTransfer-1.2.3-delta.nupkg"
			f.replaceReleaseAsset(name, zipBytes(t, map[string][]byte{
				"[Content_Types].xml": {}, "_rels/.rels": {}, packageID + ".nuspec": []byte(packageMetadata("1.2.3", channel, productMainExecutableName)),
			}))
		}},
		{"stale Portable", func(f *bundleFixture) {
			f.files[portableName] = makePortable(t, "1.2.2", channel)
			f.reseal()
		}},
		{"arbitrary Setup blob", func(f *bundleFixture) {
			f.files[setupName] = []byte("not a PE image")
			f.reseal()
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBundleFixture(t, "1.2.3", true)
			test.edit(fixture)
			if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
				t.Fatalf("error = %v, want %v", err, ErrMetadata)
			}
		})
	}
}

func TestValidatorAcceptsHistoricalLegacyFullAndCurrentDeltaReplacement(t *testing.T) {
	fixture := newBundleFixtureWithAssets(t, []fixtureAsset{
		{version: "1.2.2", kind: "Full", layout: fixtureLayoutLegacy},
		{version: "1.2.3", kind: "Full", layout: fixtureLayoutRenamed},
		{version: "1.2.3", kind: "Delta", layout: fixtureLayoutRenamed, baseLayout: fixtureLayoutLegacy},
	})
	deltaName := fmt.Sprintf("%s-1.2.3-delta.nupkg", packageID)
	entries := readZipBytes(t, fixture.files[deltaName])
	delete(entries, "lib/app/"+productLegacyExecutableName+".diff")
	delete(entries, "lib/app/"+productLegacyExecutableName+".shasum")
	entries["lib/app/"+productLegacyExecutableName] = makeSetupImage()
	fixture.replaceReleaseAsset(deltaName, zipBytes(t, entries))

	if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); err != nil {
		t.Fatal(err)
	}
}

func TestValidatorRejectsCurrentPackagesThatDoNotExpressRenameMigration(t *testing.T) {
	tests := []struct {
		name string
		edit func(*bundleFixture)
	}{
		{"current Full uses historical layout", func(f *bundleFixture) {
			name := fmt.Sprintf("%s-1.2.3-full.nupkg", packageID)
			f.replaceReleaseAsset(name, makeNupkg(t, "1.2.3", "Full", channel, fixtureLayoutLegacy, ""))
		}},
		{"current Full lacks compatibility launcher", func(f *bundleFixture) {
			name := fmt.Sprintf("%s-1.2.3-full.nupkg", packageID)
			entries := readZipBytes(t, f.files[name])
			delete(entries, "lib/app/"+productLegacyExecutableName)
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"current Full mixes legacy business companions", func(f *bundleFixture) {
			name := fmt.Sprintf("%s-1.2.3-full.nupkg", packageID)
			entries := readZipBytes(t, f.files[name])
			entries["lib/app/"+productLegacyAssemblyName+".dll"] = []byte("stale legacy assembly")
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"Portable lacks compatibility launcher", func(f *bundleFixture) {
			entries := readZipBytes(t, f.files[portableName])
			delete(entries, "current/"+productLegacyExecutableName)
			f.files[portableName] = zipBytes(t, entries)
			f.reseal()
		}},
		{"Portable uses legacy root launcher name", func(f *bundleFixture) {
			entries := readZipBytes(t, f.files[portableName])
			delete(entries, productMainExecutableName)
			entries[packageID+".exe"] = makeSetupImage()
			f.files[portableName] = zipBytes(t, entries)
			f.reseal()
		}},
		{"Delta patches renamed executable instead of compatibility path", func(f *bundleFixture) {
			name := fmt.Sprintf("%s-1.2.3-delta.nupkg", packageID)
			entries := readZipBytes(t, f.files[name])
			delete(entries, "lib/app/"+productLegacyExecutableName+".diff")
			delete(entries, "lib/app/"+productLegacyExecutableName+".shasum")
			entries["lib/app/"+productMainExecutableName+".diff"] = []byte("wrong baseline")
			entries["lib/app/"+productMainExecutableName+".shasum"] = []byte(strings.Repeat("0", 64))
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"Delta uses historical layout", func(f *bundleFixture) {
			name := fmt.Sprintf("%s-1.2.3-delta.nupkg", packageID)
			entries := readZipBytes(t, f.files[name])
			delete(entries, "lib/app/"+productMainExecutableName)
			delete(entries, "lib/app/"+productMainDLLName)
			delete(entries, "lib/app/"+productMainAssemblyName+".deps.json")
			delete(entries, "lib/app/"+productMainAssemblyName+".runtimeconfig.json")
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"Delta has unexpected executable", func(f *bundleFixture) {
			name := fmt.Sprintf("%s-1.2.3-delta.nupkg", packageID)
			entries := readZipBytes(t, f.files[name])
			entries["lib/app/evil.exe"] = makeSetupImage()
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"Delta has both patch encodings for one target", func(f *bundleFixture) {
			name := fmt.Sprintf("%s-1.2.3-delta.nupkg", packageID)
			entries := readZipBytes(t, f.files[name])
			entries["lib/app/"+productLegacyExecutableName+".zsdiff"] = []byte("duplicate patch encoding")
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBundleFixture(t, "1.2.3", true)
			test.edit(fixture)
			if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
				t.Fatalf("error = %v, want %v", err, ErrMetadata)
			}
		})
	}
}

func TestValidatorRequiresPublisherExecutablesAndRejectsUnexpectedExecutables(t *testing.T) {
	tests := []struct {
		name string
		edit func(*bundleFixture)
	}{
		{"Full missing publisher executable", func(f *bundleFixture) {
			name := "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg"
			entries := readZipBytes(t, f.files[name])
			delete(entries, "lib/app/Squirrel.exe")
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"Full has unexpected executable", func(f *bundleFixture) {
			name := "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg"
			entries := readZipBytes(t, f.files[name])
			entries["lib/app/evil.exe"] = makeSetupImage()
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"Portable missing publisher executable", func(f *bundleFixture) {
			entries := readZipBytes(t, f.files[portableName])
			delete(entries, "Update.exe")
			f.files[portableName] = zipBytes(t, entries)
			f.reseal()
		}},
		{"Portable has unexpected executable", func(f *bundleFixture) {
			entries := readZipBytes(t, f.files[portableName])
			entries["current/evil.exe"] = makeSetupImage()
			f.files[portableName] = zipBytes(t, entries)
			f.reseal()
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBundleFixture(t, "1.2.3", false)
			test.edit(fixture)
			if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
				t.Fatalf("error = %v, want %v", err, ErrMetadata)
			}
		})
	}
}

func TestValidatorRequiresPortableX64MachineArchitecture(t *testing.T) {
	fixture := newBundleFixture(t, "1.2.3", false)
	entries := readZipBytes(t, fixture.files[portableName])
	entries["current/sq.version"] = []byte(strings.Replace(packageMetadata("1.2.3", channel, productMainExecutableName), "<machineArchitecture>x64</machineArchitecture>", "", 1))
	fixture.files[portableName] = zipBytes(t, entries)
	fixture.reseal()
	if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
		t.Fatalf("error = %v, want %v", err, ErrMetadata)
	}
}

func TestValidatorRejectsInvalidUTF8InAllowedJSONValue(t *testing.T) {
	fixture := newBundleFixture(t, "1.2.3", false)
	fixture.files[releaseFeedName] = bytes.Replace(
		fixture.files[releaseFeedName], []byte(`"NotesMarkdown":"notes"`),
		[]byte{'"', 'N', 'o', 't', 'e', 's', 'M', 'a', 'r', 'k', 'd', 'o', 'w', 'n', '"', ':', '"', 0xff, '"'}, 1)
	fixture.reseal()
	if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
		t.Fatalf("error = %v, want %v", err, ErrMetadata)
	}
}

func TestValidatorRejectsNFCPathCollisionAndWindowsReparseAttribute(t *testing.T) {
	tests := []struct {
		name string
		edit func(*bundleFixture)
	}{
		{"NFC collision", func(f *bundleFixture) {
			name := "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg"
			entries := readZipBytes(t, f.files[name])
			entries["lib/app/caf\u00e9.dll"] = []byte("one")
			entries["lib/app/cafe\u0301.dll"] = []byte("two")
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"Windows case collision", func(f *bundleFixture) {
			name := "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg"
			entries := readZipBytes(t, f.files[name])
			entries["lib/app/Helper.dll"] = []byte("one")
			entries["lib/app/helper.dll"] = []byte("two")
			f.replaceReleaseAsset(name, zipBytes(t, entries))
		}},
		{"Windows reparse attribute", func(f *bundleFixture) {
			name := "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg"
			entries := readZipBytes(t, f.files[name])
			reparseName := "lib/app/reparse.bin"
			entries[reparseName] = []byte("target")
			f.replaceReleaseAsset(name, zipBytesWithReparseEntry(t, entries, reparseName))
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBundleFixture(t, "1.2.3", false)
			test.edit(fixture)
			if _, err := fixture.validator.Validate(context.Background(), fixture.envelope, ""); !errorsIs(err, ErrMetadata) {
				t.Fatalf("error = %v, want %v", err, ErrMetadata)
			}
		})
	}
}

func TestCanonicalInnerWindowsPathUsesNFCAndUTF16Lengths(t *testing.T) {
	longUnicodeComponent := strings.Repeat("界", 200)
	canonical, ok := canonicalInnerWindowsPath("root/" + longUnicodeComponent)
	if !ok || canonical == "" {
		t.Fatal("valid 200 UTF-16-unit component was rejected by byte length")
	}
	precomposed, ok := canonicalInnerWindowsPath("caf\u00e9.txt")
	if !ok {
		t.Fatal("precomposed name rejected")
	}
	decomposed, ok := canonicalInnerWindowsPath("cafe\u0301.txt")
	if !ok || decomposed != precomposed {
		t.Fatalf("NFC canonicalization = %q, want %q", decomposed, precomposed)
	}
	if _, ok := canonicalInnerWindowsPath(strings.Repeat("界", 256)); ok {
		t.Fatal("component above 255 UTF-16 units was accepted")
	}
	if _, ok := canonicalInnerWindowsPath(strings.Repeat("e\u0301", 128)); ok {
		t.Fatal("raw component above 255 UTF-16 units was accepted after NFC contraction")
	}
}

func TestInnerZipPreflightRejectsEntryCountAndZIP64SizeBeforeReader(t *testing.T) {
	tooMany := make([]byte, 22)
	binary.LittleEndian.PutUint32(tooMany, 0x06054b50)
	binary.LittleEndian.PutUint16(tooMany[8:], maxInnerEntries+1)
	binary.LittleEndian.PutUint16(tooMany[10:], maxInnerEntries+1)
	if _, err := preflightInnerArchive(bytes.NewReader(tooMany), int64(len(tooMany))); err == nil {
		t.Fatal("preflight accepted entry count above cap")
	}

	zip64 := make([]byte, 98)
	binary.LittleEndian.PutUint32(zip64[0:], 0x06064b50)
	binary.LittleEndian.PutUint64(zip64[4:], 44)
	binary.LittleEndian.PutUint64(zip64[24:], 1)
	binary.LittleEndian.PutUint64(zip64[32:], 1)
	binary.LittleEndian.PutUint64(zip64[40:], math.MaxUint64)
	binary.LittleEndian.PutUint64(zip64[48:], 0)
	binary.LittleEndian.PutUint32(zip64[56:], 0x07064b50)
	binary.LittleEndian.PutUint64(zip64[64:], 0)
	binary.LittleEndian.PutUint32(zip64[72:], 1)
	binary.LittleEndian.PutUint32(zip64[76:], 0x06054b50)
	binary.LittleEndian.PutUint16(zip64[84:], math.MaxUint16)
	binary.LittleEndian.PutUint16(zip64[86:], math.MaxUint16)
	binary.LittleEndian.PutUint32(zip64[88:], math.MaxUint32)
	binary.LittleEndian.PutUint32(zip64[92:], math.MaxUint32)
	if _, err := preflightInnerArchive(bytes.NewReader(zip64), int64(len(zip64))); err == nil {
		t.Fatal("preflight accepted overflowing ZIP64 central directory size")
	}

	central := make([]byte, 67+22)
	binary.LittleEndian.PutUint32(central[0:], 0x02014b50)
	binary.LittleEndian.PutUint32(central[20:], math.MaxUint32)
	binary.LittleEndian.PutUint32(central[24:], math.MaxUint32)
	binary.LittleEndian.PutUint16(central[28:], 1)
	binary.LittleEndian.PutUint16(central[30:], 20)
	central[46] = 'a'
	binary.LittleEndian.PutUint16(central[47:], 0x0001)
	binary.LittleEndian.PutUint16(central[49:], 16)
	binary.LittleEndian.PutUint64(central[51:], math.MaxUint64)
	binary.LittleEndian.PutUint64(central[59:], math.MaxUint64)
	binary.LittleEndian.PutUint32(central[67:], 0x06054b50)
	binary.LittleEndian.PutUint16(central[75:], 1)
	binary.LittleEndian.PutUint16(central[77:], 1)
	binary.LittleEndian.PutUint32(central[79:], 67)
	if _, err := preflightInnerArchive(bytes.NewReader(central), int64(len(central))); err == nil {
		t.Fatal("preflight accepted overflowing ZIP64 entry sizes")
	}

	nested := zipBytes(t, map[string][]byte{"safe.txt": []byte("safe")})
	fakeEnd := make([]byte, 22)
	binary.LittleEndian.PutUint32(fakeEnd, 0x06054b50)
	binary.LittleEndian.PutUint16(fakeEnd[8:], math.MaxUint16)
	binary.LittleEndian.PutUint16(fakeEnd[10:], math.MaxUint16)
	comment := append(fakeEnd, make([]byte, 22)...)
	binary.LittleEndian.PutUint16(nested[len(nested)-2:], uint16(len(comment)))
	nested = append(nested, comment...)
	if _, err := preflightInnerArchive(bytes.NewReader(nested), int64(len(nested))); err == nil {
		t.Fatal("preflight accepted a later fake EOCD that archive/zip would select")
	}
}

type replacingContext struct {
	context.Context
	target string
	atCall int
	calls  int
	err    error
}

func (ctx *replacingContext) Err() error {
	ctx.calls++
	if ctx.calls == ctx.atCall {
		changed := time.Now().Add(24 * time.Hour)
		ctx.err = os.Chtimes(ctx.target, changed, changed)
	}
	return nil
}

func (ctx *replacingContext) Deadline() (time.Time, bool) { return time.Time{}, false }
func (ctx *replacingContext) Done() <-chan struct{}       { return nil }
func (ctx *replacingContext) Value(key any) any           { return nil }

func TestValidatorRejectsEnvelopeFileMetadataChangeAfterSecureOpening(t *testing.T) {
	fixture := newBundleFixture(t, "1.2.3", false)
	target := filepath.Join(fixture.root, releaseFeedName)
	ctx := &replacingContext{
		Context: context.Background(), target: target,
		atCall: len(fixture.envelope.Entries) + 1,
	}
	if _, err := fixture.validator.Validate(ctx, fixture.envelope, ""); err == nil {
		t.Fatalf("Validate accepted an envelope file changed after secure opening (calls=%d)", ctx.calls)
	}
	if ctx.err != nil {
		t.Fatalf("replace path: %v", ctx.err)
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

func (fixture *bundleFixture) replaceReleaseAsset(name string, data []byte) {
	fixture.t.Helper()
	fixture.files[name] = data
	var document releaseDocument
	if err := json.Unmarshal(fixture.files[releaseFeedName], &document); err != nil {
		fixture.t.Fatal(err)
	}
	for index := range document.Assets {
		asset := &document.Assets[index]
		if asset.FileName != nil && *asset.FileName == name {
			sha1Value := strings.ToUpper(sha1Hex(data))
			sha256Value := strings.ToUpper(sha256Hex(data))
			size := int64(len(data))
			asset.SHA1, asset.SHA256, asset.Size = &sha1Value, &sha256Value, &size
		}
	}
	encoded, err := json.Marshal(document)
	if err != nil {
		fixture.t.Fatal(err)
	}
	fixture.files[releaseFeedName] = encoded
	var fulls []fullAsset
	for _, asset := range document.Assets {
		if asset.Type != nil && *asset.Type == "Full" {
			parsed, _ := parseVersion(*asset.Version)
			fulls = append(fulls, fullAsset{name: *asset.FileName, parsed: parsed})
		}
	}
	sort.Slice(fulls, func(i, j int) bool { return fulls[i].parsed.compare(fulls[j].parsed) < 0 })
	var releases strings.Builder
	for _, full := range fulls {
		fmt.Fprintf(&releases, "%s %s %d\n", strings.ToUpper(sha1Hex(fixture.files[full.name])), full.name, len(fixture.files[full.name]))
	}
	fixture.files[releasesIndexName] = []byte(releases.String())
	fixture.reseal()
}

func sha256Hex(data []byte) string {
	digest := sha256.Sum256(data)
	return hex.EncodeToString(digest[:])
}
func sha1Hex(data []byte) string { digest := sha1.Sum(data); return hex.EncodeToString(digest[:]) }
func errorsIs(err, target error) bool {
	return err != nil && (err == target || strings.Contains(err.Error(), target.Error()))
}
