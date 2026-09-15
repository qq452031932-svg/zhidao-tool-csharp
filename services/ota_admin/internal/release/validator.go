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

// MaximumNotesBytes is shared with persistence. Validation uses bytes while
// SQLite length counts characters; valid UTF-8 with this byte cap always fits
// the database character cap.
const MaximumNotesBytes = 65_536

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
	NotesMarkdown       string       `json:"notesMarkdown"`
	ManifestSHA256      string       `json:"manifestSha256"`
	PublisherSPKISHA256 string       `json:"publisherSpkiSha256"`
	FileCount           int          `json:"fileCount"`
	Files               []ReportFile `json:"files"`
}

type verifiedFile struct {
	name   string
	path   string
	file   *os.File
	info   os.FileInfo
	size   int64
	sha256 string
	sha1   string
}

type loadedEnvelope struct {
	rootPath       string
	root           *os.File
	rootInfo       os.FileInfo
	descriptorRoot bool
	files          map[string]*verifiedFile
}

func NewValidator(trust *publisher.Trust) *Validator { return &Validator{trust: trust} }

func (validator *Validator) Validate(ctx context.Context, envelope *upload.Envelope, highestCommittedVersion string) (Report, error) {
	return validator.validate(ctx, envelope, nil, highestCommittedVersion)
}

// ValidateOpenedDirectory validates an envelope against a caller-owned,
// already opened directory descriptor. It is used by the Linux publisher so
// validation never has to reopen a generation through /proc or a mutable path.
// The caller retains ownership of directory.
func (validator *Validator) ValidateOpenedDirectory(ctx context.Context, envelope *upload.Envelope, directory *os.File, highestCommittedVersion string) (Report, error) {
	if directory == nil {
		return Report{}, fmt.Errorf("%w: opened directory is required", ErrManifest)
	}
	return validator.validate(ctx, envelope, directory, highestCommittedVersion)
}

func (validator *Validator) validate(ctx context.Context, envelope *upload.Envelope, directory *os.File, highestCommittedVersion string) (Report, error) {
	if validator == nil || validator.trust == nil || envelope == nil || envelope.Verified {
		return Report{}, fmt.Errorf("%w: validator input", ErrManifest)
	}
	loaded, err := loadEnvelope(ctx, envelope, directory)
	if err != nil {
		return Report{}, fmt.Errorf("%w: %v", ErrManifest, err)
	}
	defer loaded.close()
	files := loaded.files
	for _, required := range []string{manifestName, signatureName, releaseFeedName, assetCatalogName, releasesIndexName, setupName, portableName} {
		if _, ok := files[required]; !ok {
			return Report{}, fmt.Errorf("%w: missing %s", ErrManifest, required)
		}
	}
	manifest, err := readLimited(files[manifestName], 81_788)
	if err != nil {
		return Report{}, fmt.Errorf("%w: manifest read", ErrManifest)
	}
	signature, err := readLimited(files[signatureName], 384)
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
		sha256Value, sha1Value, err := hashFile(ctx, file)
		if err != nil || sha256Value != item.SHA256 {
			return Report{}, fmt.Errorf("%w: hash %s", ErrManifest, item.Name)
		}
		file.sha256, file.sha1 = sha256Value, sha1Value
		reportFiles = append(reportFiles, ReportFile{Name: item.Name, Size: file.size, SHA256: sha256Value})
	}

	feedBytes, err := readLimited(files[releaseFeedName], maxMetadataBytes)
	if err != nil {
		return Report{}, fmt.Errorf("%w: feed read", ErrMetadata)
	}
	var feed releaseDocument
	if err := decodeStrictJSON(feedBytes, &feed); err != nil || len(feed.Assets) == 0 || len(feed.Assets) > 254 {
		return Report{}, fmt.Errorf("%w: feed JSON", ErrMetadata)
	}
	currentVersion, notesMarkdown, fullAssets, releaseFiles, err := validateReleaseAssets(feed.Assets, files)
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
	// Linux authenticates package bytes through the signed manifest/SPKI root.
	// This gate validates bounded Velopack structure and exact production identity;
	// the Windows publisher remains responsible for full Authenticode verification.
	if err := validateSetupStructure(files[setupName]); err != nil {
		return Report{}, fmt.Errorf("%w: %v", ErrMetadata, err)
	}
	fullLayouts := make(map[string]packageLayout, len(fullAssets))
	for _, asset := range feed.Assets {
		if *asset.Type != "Full" {
			continue
		}
		layout, layoutErr := classifyFullPackageStructure(files[*asset.FileName], *asset.Version)
		if layoutErr != nil {
			return Report{}, fmt.Errorf("%w: %s: %v", ErrMetadata, *asset.FileName, layoutErr)
		}
		fullLayouts[*asset.Version] = layout
	}
	currentLayout := fullLayouts[currentVersion]
	if currentLayout != packageLayoutRenamed {
		return Report{}, fmt.Errorf("%w: current Full package does not use the renamed layout", ErrMetadata)
	}
	if err := validatePortableStructure(files[portableName], currentVersion); err != nil {
		return Report{}, fmt.Errorf("%w: %v", ErrMetadata, err)
	}
	currentParsed, _ := parseVersion(currentVersion)
	baseVersion := version{}
	baseLayout := packageLayout(0)
	for _, full := range fullAssets {
		if full.parsed.compare(currentParsed) >= 0 || baseLayout != 0 && full.parsed.compare(baseVersion) <= 0 {
			continue
		}
		baseVersion = full.parsed
		baseLayout = fullLayouts[*releaseFiles[full.name].Version]
	}
	for _, asset := range feed.Assets {
		if *asset.Type != "Delta" {
			continue
		}
		if *asset.Version != currentVersion {
			return Report{}, fmt.Errorf("%w: historical Delta packages are not allowed", ErrMetadata)
		}
		if err := validateDeltaPackageStructure(files[*asset.FileName], *asset.Version, baseLayout, currentLayout); err != nil {
			return Report{}, fmt.Errorf("%w: %s: %v", ErrMetadata, *asset.FileName, err)
		}
	}
	catalogBytes, err := readLimited(files[assetCatalogName], maxMetadataBytes)
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

	indexBytes, err := readLimited(files[releasesIndexName], maxMetadataBytes)
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
		if actual.Name != expected.name || actual.Size != file.size || actual.SHA1 != strings.ToUpper(file.sha1) {
			return Report{}, fmt.Errorf("%w: %s", ErrReleasesIndex, expected.name)
		}
	}
	if highestCommittedVersion != "" {
		floor, floorErr := parseVersion(highestCommittedVersion)
		current, currentErr := parseVersion(currentVersion)
		if floorErr != nil || currentErr != nil || current.compare(floor) <= 0 {
			return Report{}, ErrVersionFloor
		}
	}
	if err := loaded.verifyStable(); err != nil {
		return Report{}, fmt.Errorf("%w: envelope changed after validation", ErrManifest)
	}
	manifestDigest := sha256.Sum256(manifest)
	return Report{
		SchemaVersion: 1, Verified: true, PackageID: packageID, Channel: channel, Version: currentVersion, NotesMarkdown: notesMarkdown,
		ManifestSHA256: hex.EncodeToString(manifestDigest[:]), PublisherSPKISHA256: validator.trust.SPKISHA256(),
		FileCount: len(files), Files: reportFiles,
	}, nil
}

func loadEnvelope(ctx context.Context, envelope *upload.Envelope, openedRoot *os.File) (*loadedEnvelope, error) {
	root, err := filepath.Abs(envelope.ExtractionRoot)
	if err != nil || !filepath.IsAbs(root) {
		return nil, errors.New("extraction root is invalid")
	}
	root = filepath.Clean(root)
	rootFile := openedRoot
	descriptorRoot := rootFile != nil
	var rootInfo os.FileInfo
	if descriptorRoot {
		rootInfo, err = rootFile.Stat()
		if err != nil || !rootInfo.IsDir() {
			return nil, errors.New("opened extraction root is invalid")
		}
	} else {
		pathInfo, pathErr := os.Lstat(root)
		if pathErr != nil || !pathInfo.IsDir() || pathInfo.Mode()&os.ModeSymlink != 0 {
			return nil, errors.New("extraction root changed")
		}
		rootFile, err = openDirectoryNoFollow(root)
		if err != nil {
			return nil, errors.New("extraction root cannot be securely opened")
		}
		rootInfo, err = rootFile.Stat()
		if err != nil || !rootInfo.IsDir() || !os.SameFile(pathInfo, rootInfo) {
			rootFile.Close()
			return nil, errors.New("extraction root identity changed")
		}
	}
	loaded := &loadedEnvelope{rootPath: root, root: rootFile, rootInfo: rootInfo, descriptorRoot: descriptorRoot, files: make(map[string]*verifiedFile, len(envelope.Entries))}
	failed := true
	defer func() {
		if failed {
			loaded.close()
		}
	}()
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
		var pathInfo os.FileInfo
		if !descriptorRoot {
			pathInfo, err = os.Lstat(entry.Path)
			if err != nil || !pathInfo.Mode().IsRegular() || pathInfo.Mode()&os.ModeSymlink != 0 || pathInfo.Size() != entry.Size {
				return nil, errors.New("entry metadata changed")
			}
		}
		file, err := openChildNoFollow(rootFile, entry.Path, entry.Name)
		if err != nil {
			return nil, errors.New("entry cannot be securely opened")
		}
		info, err := file.Stat()
		if err != nil || !info.Mode().IsRegular() || info.Size() != entry.Size || !descriptorRoot && !os.SameFile(pathInfo, info) {
			file.Close()
			return nil, errors.New("entry descriptor identity changed")
		}
		folded[key] = struct{}{}
		loaded.files[entry.Name] = &verifiedFile{name: entry.Name, path: entry.Path, file: file, info: info, size: info.Size()}
	}
	failed = false
	return loaded, nil
}

func (loaded *loadedEnvelope) close() {
	if loaded == nil {
		return
	}
	for _, file := range loaded.files {
		_ = file.file.Close()
	}
	if loaded.root != nil && !loaded.descriptorRoot {
		_ = loaded.root.Close()
	}
}

func (loaded *loadedEnvelope) verifyStable() error {
	rootDescriptor, err := loaded.root.Stat()
	if err != nil || !os.SameFile(loaded.rootInfo, rootDescriptor) {
		return errors.New("extraction root identity changed")
	}
	if !loaded.descriptorRoot {
		rootPath, pathErr := os.Lstat(loaded.rootPath)
		if pathErr != nil || !os.SameFile(rootDescriptor, rootPath) || rootPath.Mode()&os.ModeSymlink != 0 {
			return errors.New("extraction root identity changed")
		}
	}
	for _, file := range loaded.files {
		descriptor, descriptorErr := file.file.Stat()
		if descriptorErr != nil || !descriptor.Mode().IsRegular() || !os.SameFile(file.info, descriptor) || descriptor.Size() != file.size ||
			descriptor.ModTime() != file.info.ModTime() {
			return fmt.Errorf("entry identity changed: %s", file.name)
		}
		if !loaded.descriptorRoot {
			pathInfo, pathErr := os.Lstat(file.path)
			if pathErr != nil || pathInfo.Mode()&os.ModeSymlink != 0 || !os.SameFile(descriptor, pathInfo) {
				return fmt.Errorf("entry identity changed: %s", file.name)
			}
		}
	}
	return nil
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

func validateReleaseAssets(assets []releaseAsset, files map[string]*verifiedFile) (string, string, []fullAsset, map[string]releaseAsset, error) {
	current := version{}
	currentText := ""
	releaseFiles := make(map[string]releaseAsset, len(assets))
	foldedFiles := make(map[string]struct{}, len(assets))
	identities := make(map[string]struct{}, len(assets))
	fulls := make([]fullAsset, 0, len(assets))
	for _, asset := range assets {
		if asset.PackageID == nil || asset.Version == nil || asset.Type == nil || asset.FileName == nil || asset.SHA1 == nil || asset.SHA256 == nil || asset.Size == nil ||
			*asset.PackageID != packageID || (*asset.Type != "Full" && *asset.Type != "Delta") || *asset.Size < 0 {
			return "", "", nil, nil, ErrMetadata
		}
		parsed, err := parseVersion(*asset.Version)
		if err != nil {
			return "", "", nil, nil, ErrMetadata
		}
		expectedName := fmt.Sprintf("%s-%s-%s.nupkg", packageID, *asset.Version, strings.ToLower(*asset.Type))
		file, ok := files[*asset.FileName]
		if !ok || *asset.FileName != expectedName || *asset.Size != file.size || !exactHex(*asset.SHA256, 64, true) || !exactHex(*asset.SHA1, 40, true) {
			return "", "", nil, nil, ErrMetadata
		}
		folded := strings.ToLower(*asset.FileName)
		identity := *asset.Version + ":" + *asset.Type
		if _, duplicate := foldedFiles[folded]; duplicate {
			return "", "", nil, nil, ErrMetadata
		}
		if _, duplicate := identities[identity]; duplicate {
			return "", "", nil, nil, ErrMetadata
		}
		if !strings.EqualFold(*asset.SHA256, file.sha256) || *asset.SHA1 != strings.ToUpper(file.sha1) {
			return "", "", nil, nil, ErrMetadata
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
	fullVersions := make(map[string]version, len(fulls))
	for _, full := range fulls {
		if _, duplicate := fullVersions[full.name]; duplicate {
			return "", "", nil, nil, ErrMetadata
		}
		fullVersions[full.name] = full.parsed
	}
	currentFull := packageID + "-" + currentText + "-full.nupkg"
	currentAsset, ok := releaseFiles[currentFull]
	if !ok || currentAsset.NotesMarkdown == nil || len(*currentAsset.NotesMarkdown) > MaximumNotesBytes {
		return "", "", nil, nil, ErrMetadata
	}
	notesMarkdown := *currentAsset.NotesMarkdown
	for _, asset := range releaseFiles {
		if *asset.Version == currentText && (asset.NotesMarkdown == nil || *asset.NotesMarkdown != notesMarkdown) {
			return "", "", nil, nil, fmt.Errorf("%w: current release notes disagree", ErrMetadata)
		}
	}
	hasDelta := false
	hasCurrentDelta := false
	for name, asset := range releaseFiles {
		if *asset.Type != "Delta" {
			continue
		}
		hasDelta = true
		deltaVersion, _ := parseVersion(*asset.Version)
		if _, ok := fullVersions[packageID+"-"+*asset.Version+"-full.nupkg"]; !ok {
			return "", "", nil, nil, fmt.Errorf("%w: delta has no same-version Full %s", ErrMetadata, name)
		}
		hasLowerFull := false
		for _, fullVersion := range fullVersions {
			if fullVersion.compare(deltaVersion) < 0 {
				hasLowerFull = true
				break
			}
		}
		if !hasLowerFull {
			return "", "", nil, nil, fmt.Errorf("%w: delta has no lower Full base %s", ErrMetadata, name)
		}
		if *asset.Version == currentText {
			hasCurrentDelta = true
		}
	}
	if hasDelta && !hasCurrentDelta {
		return "", "", nil, nil, fmt.Errorf("%w: current Full has no Delta", ErrMetadata)
	}
	return currentText, notesMarkdown, fulls, releaseFiles, nil
}

func validateCatalog(catalog []catalogAsset, currentVersion string, releaseFiles map[string]releaseAsset, files map[string]*verifiedFile) error {
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

func readLimited(file *verifiedFile, limit int64) ([]byte, error) {
	if _, err := file.file.Seek(0, io.SeekStart); err != nil {
		return nil, err
	}
	data, err := io.ReadAll(io.LimitReader(file.file, limit+1))
	if err != nil || int64(len(data)) > limit {
		return nil, errors.New("file exceeds limit")
	}
	return data, nil
}

func hashFile(ctx context.Context, file *verifiedFile) (string, string, error) {
	if _, err := file.file.Seek(0, io.SeekStart); err != nil {
		return "", "", err
	}
	sha256Hash := sha256.New()
	sha1Hash := sha1.New()
	writer := io.MultiWriter(sha256Hash, sha1Hash)
	buffer := make([]byte, 64<<10)
	for {
		if err := ctx.Err(); err != nil {
			return "", "", err
		}
		count, readErr := file.file.Read(buffer)
		if count > 0 {
			_, _ = writer.Write(buffer[:count])
		}
		if errors.Is(readErr, io.EOF) {
			break
		}
		if readErr != nil {
			return "", "", readErr
		}
	}
	return hex.EncodeToString(sha256Hash.Sum(nil)), hex.EncodeToString(sha1Hash.Sum(nil)), nil
}
