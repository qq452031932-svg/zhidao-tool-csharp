package release

import (
	"context"
	"crypto/sha1"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

var (
	ErrSignature     = errors.New("publisher signature mismatch")
	ErrManifest      = errors.New("manifest validation failed")
	ErrMetadata      = errors.New("release metadata validation failed")
	ErrCatalog       = errors.New("asset catalog validation failed")
	ErrReleasesIndex = errors.New("RELEASES validation failed")
	ErrVersionFloor  = errors.New("version does not exceed historical floor")
)

type Validator struct{ trust *publisher.Trust }

type ReportFile struct {
	Name   string `json:"name"`
	Size   int64  `json:"size"`
	SHA256 string `json:"sha256"`
}

type Report struct {
	SchemaVersion       int          `json:"schemaVersion"`
	Verified            bool         `json:"verified"`
	PackageID           string       `json:"packageId"`
	Channel             string       `json:"channel"`
	Version             string       `json:"version"`
	ManifestSHA256      string       `json:"manifestSha256"`
	PublisherSPKISHA256 string       `json:"publisherSpkiSha256"`
	FileCount           int          `json:"fileCount"`
	Files               []ReportFile `json:"files"`
}

type verifiedFile struct {
	name   string
	path   string
	size   int64
	sha256 string
	sha1   string
}

func NewValidator(trust *publisher.Trust) *Validator { return &Validator{trust: trust} }

func (validator *Validator) Validate(ctx context.Context, envelope *upload.Envelope, highestCommittedVersion string) (Report, error) {
	if validator == nil || validator.trust == nil || envelope == nil || envelope.Verified {
		return Report{}, fmt.Errorf("%w: validator input", ErrManifest)
	}
	files, err := loadEnvelope(ctx, envelope)
	if err != nil {
		return Report{}, fmt.Errorf("%w: %v", ErrManifest, err)
	}
	for _, required := range []string{manifestName, signatureName, releaseFeedName, assetCatalogName, releasesIndexName, setupName, portableName} {
		if _, ok := files[required]; !ok {
			return Report{}, fmt.Errorf("%w: missing %s", ErrManifest, required)
		}
	}
	manifest, err := readLimited(files[manifestName].path, 81_788)
	if err != nil {
		return Report{}, fmt.Errorf("%w: manifest read", ErrManifest)
	}
	signature, err := readLimited(files[signatureName].path, 384)
	if err != nil || len(signature) != 384 {
		return Report{}, ErrSignature
	}
	manifestEntries, err := validator.trust.VerifyManifest(manifest, signature)
	if err != nil {
		if errors.Is(err, publisher.ErrSignature) {
			return Report{}, ErrSignature
		}
		return Report{}, fmt.Errorf("%w: %v", ErrManifest, err)
	}
	if len(manifestEntries) != len(files)-2 {
		return Report{}, fmt.Errorf("%w: closure count", ErrManifest)
	}
	reportFiles := make([]ReportFile, 0, len(manifestEntries))
	for _, item := range manifestEntries {
		file, ok := files[item.Name]
		if !ok || item.Name == manifestName || item.Name == signatureName {
			return Report{}, fmt.Errorf("%w: closure member", ErrManifest)
		}
		digest, err := hashFile(ctx, file.path, sha256.New())
		if err != nil || digest != item.SHA256 {
			return Report{}, fmt.Errorf("%w: hash %s", ErrManifest, item.Name)
		}
		file.sha256 = digest
		files[item.Name] = file
		reportFiles = append(reportFiles, ReportFile{Name: item.Name, Size: file.size, SHA256: digest})
	}

	feedBytes, err := readLimited(files[releaseFeedName].path, maxMetadataBytes)
	if err != nil {
		return Report{}, fmt.Errorf("%w: feed read", ErrMetadata)
	}
	var feed releaseDocument
	if err := decodeStrictJSON(feedBytes, &feed); err != nil || len(feed.Assets) == 0 || len(feed.Assets) > 254 {
		return Report{}, fmt.Errorf("%w: feed JSON", ErrMetadata)
	}
	currentVersion, fullAssets, releaseFiles, err := validateReleaseAssets(ctx, feed.Assets, files)
	if err != nil {
		return Report{}, err
	}
	for name := range files {
		if strings.HasSuffix(name, ".nupkg") {
			if _, ok := releaseFiles[name]; !ok {
				return Report{}, fmt.Errorf("%w: unreferenced package %s", ErrMetadata, name)
			}
		}
	}
	if highestCommittedVersion != "" {
		floor, floorErr := parseVersion(highestCommittedVersion)
		current, currentErr := parseVersion(currentVersion)
		if floorErr != nil || currentErr != nil || current.compare(floor) <= 0 {
			return Report{}, ErrVersionFloor
		}
	}

	catalogBytes, err := readLimited(files[assetCatalogName].path, maxMetadataBytes)
	if err != nil {
		return Report{}, fmt.Errorf("%w: catalog read", ErrCatalog)
	}
	var catalog []catalogAsset
	if err := decodeStrictJSON(catalogBytes, &catalog); err != nil {
		return Report{}, fmt.Errorf("%w: catalog JSON", ErrCatalog)
	}
	if err := validateCatalog(catalog, currentVersion, releaseFiles, files); err != nil {
		return Report{}, err
	}

	indexBytes, err := readLimited(files[releasesIndexName].path, maxMetadataBytes)
	if err != nil {
		return Report{}, fmt.Errorf("%w: index read", ErrReleasesIndex)
	}
	index, err := parseReleasesIndex(indexBytes)
	if err != nil || len(index) != len(fullAssets) {
		return Report{}, fmt.Errorf("%w: index closure", ErrReleasesIndex)
	}
	sort.Slice(fullAssets, func(i, j int) bool { return fullAssets[i].parsed.compare(fullAssets[j].parsed) < 0 })
	for i, expected := range fullAssets {
		actual := index[i]
		file := files[expected.name]
		sha1Value, hashErr := hashFile(ctx, file.path, sha1.New())
		if hashErr != nil || actual.Name != expected.name || actual.Size != file.size || actual.SHA1 != strings.ToUpper(sha1Value) {
			return Report{}, fmt.Errorf("%w: %s", ErrReleasesIndex, expected.name)
		}
	}
	manifestDigest := sha256.Sum256(manifest)
	return Report{
		SchemaVersion: 1, Verified: true, PackageID: packageID, Channel: channel, Version: currentVersion,
		ManifestSHA256: hex.EncodeToString(manifestDigest[:]), PublisherSPKISHA256: validator.trust.SPKISHA256(),
		FileCount: len(files), Files: reportFiles,
	}, nil
}

func loadEnvelope(ctx context.Context, envelope *upload.Envelope) (map[string]verifiedFile, error) {
	root, err := filepath.Abs(envelope.ExtractionRoot)
	if err != nil || !filepath.IsAbs(root) {
		return nil, errors.New("extraction root is invalid")
	}
	root = filepath.Clean(root)
	rootInfo, err := os.Lstat(root)
	if err != nil || !rootInfo.IsDir() || rootInfo.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("extraction root changed")
	}
	files := make(map[string]verifiedFile, len(envelope.Entries))
	folded := make(map[string]struct{}, len(envelope.Entries))
	for _, entry := range envelope.Entries {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		if !allowedName(entry.Name) || filepath.Dir(filepath.Clean(entry.Path)) != root || filepath.Base(entry.Path) != entry.Name {
			return nil, errors.New("entry name or path is invalid")
		}
		key := strings.ToLower(entry.Name)
		if _, duplicate := folded[key]; duplicate {
			return nil, errors.New("duplicate entry")
		}
		info, err := os.Lstat(entry.Path)
		if err != nil || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 || info.Size() != entry.Size {
			return nil, errors.New("entry metadata changed")
		}
		folded[key] = struct{}{}
		files[entry.Name] = verifiedFile{name: entry.Name, path: entry.Path, size: info.Size()}
	}
	return files, nil
}

func allowedName(name string) bool {
	switch name {
	case manifestName, signatureName, releaseFeedName, assetCatalogName, releasesIndexName, setupName, portableName:
		return true
	}
	match := packageFile.FindStringSubmatch(name)
	if match == nil {
		return false
	}
	_, err := parseVersion(match[1])
	return err == nil
}

type fullAsset struct {
	name   string
	parsed version
}

func validateReleaseAssets(ctx context.Context, assets []releaseAsset, files map[string]verifiedFile) (string, []fullAsset, map[string]releaseAsset, error) {
	current := version{}
	currentText := ""
	releaseFiles := make(map[string]releaseAsset, len(assets))
	foldedFiles := make(map[string]struct{}, len(assets))
	identities := make(map[string]struct{}, len(assets))
	fulls := make([]fullAsset, 0, len(assets))
	for _, asset := range assets {
		if asset.PackageID == nil || asset.Version == nil || asset.Type == nil || asset.FileName == nil || asset.SHA1 == nil || asset.SHA256 == nil || asset.Size == nil ||
			*asset.PackageID != packageID || (*asset.Type != "Full" && *asset.Type != "Delta") || *asset.Size < 0 {
			return "", nil, nil, ErrMetadata
		}
		parsed, err := parseVersion(*asset.Version)
		if err != nil {
			return "", nil, nil, ErrMetadata
		}
		expectedName := fmt.Sprintf("%s-%s-%s.nupkg", packageID, *asset.Version, strings.ToLower(*asset.Type))
		file, ok := files[*asset.FileName]
		if !ok || *asset.FileName != expectedName || *asset.Size != file.size || !exactHex(*asset.SHA256, 64, true) || !exactHex(*asset.SHA1, 40, true) {
			return "", nil, nil, ErrMetadata
		}
		folded := strings.ToLower(*asset.FileName)
		identity := *asset.Version + ":" + *asset.Type
		if _, duplicate := foldedFiles[folded]; duplicate {
			return "", nil, nil, ErrMetadata
		}
		if _, duplicate := identities[identity]; duplicate {
			return "", nil, nil, ErrMetadata
		}
		sha1Value, err := hashFile(ctx, file.path, sha1.New())
		if err != nil || !strings.EqualFold(*asset.SHA256, file.sha256) || *asset.SHA1 != strings.ToUpper(sha1Value) {
			return "", nil, nil, ErrMetadata
		}
		releaseFiles[*asset.FileName] = asset
		foldedFiles[folded] = struct{}{}
		identities[identity] = struct{}{}
		if currentText == "" || parsed.compare(current) > 0 {
			current, currentText = parsed, *asset.Version
		}
		if *asset.Type == "Full" {
			fulls = append(fulls, fullAsset{name: *asset.FileName, parsed: parsed})
		}
	}
	fullVersions := make(map[string]struct{}, len(fulls))
	for _, full := range fulls {
		if _, duplicate := fullVersions[full.name]; duplicate {
			return "", nil, nil, ErrMetadata
		}
		fullVersions[full.name] = struct{}{}
	}
	currentFull := packageID + "-" + currentText + "-full.nupkg"
	if _, ok := releaseFiles[currentFull]; !ok {
		return "", nil, nil, ErrMetadata
	}
	for name, asset := range releaseFiles {
		if *asset.Type == "Delta" && *asset.Version != currentText {
			return "", nil, nil, fmt.Errorf("%w: historical delta %s", ErrMetadata, name)
		}
	}
	return currentText, fulls, releaseFiles, nil
}

func validateCatalog(catalog []catalogAsset, currentVersion string, releaseFiles map[string]releaseAsset, files map[string]verifiedFile) error {
	expected := map[string]string{
		"Installer": setupName,
		"Full":      packageID + "-" + currentVersion + "-full.nupkg",
		"Portable":  portableName,
	}
	delta := packageID + "-" + currentVersion + "-delta.nupkg"
	if _, ok := releaseFiles[delta]; ok {
		expected["Delta"] = delta
	}
	if len(catalog) != len(expected) {
		return ErrCatalog
	}
	seen := make(map[string]struct{}, len(catalog))
	for _, item := range catalog {
		if item.Type == nil || item.RelativeFileName == nil || expected[*item.Type] != *item.RelativeFileName {
			return ErrCatalog
		}
		if _, ok := files[*item.RelativeFileName]; !ok {
			return ErrCatalog
		}
		if _, duplicate := seen[*item.Type]; duplicate {
			return ErrCatalog
		}
		seen[*item.Type] = struct{}{}
	}
	return nil
}

func readLimited(path string, limit int64) ([]byte, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, limit+1))
	if err != nil || int64(len(data)) > limit {
		return nil, errors.New("file exceeds limit")
	}
	return data, nil
}

type hashWriter interface {
	io.Writer
	Sum([]byte) []byte
}

func hashFile(ctx context.Context, path string, hash hashWriter) (string, error) {
	file, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer file.Close()
	buffer := make([]byte, 64<<10)
	for {
		if err := ctx.Err(); err != nil {
			return "", err
		}
		count, readErr := file.Read(buffer)
		if count > 0 {
			_, _ = hash.Write(buffer[:count])
		}
		if errors.Is(readErr, io.EOF) {
			break
		}
		if readErr != nil {
			return "", readErr
		}
	}
	return hex.EncodeToString(hash.Sum(nil)), nil
}
