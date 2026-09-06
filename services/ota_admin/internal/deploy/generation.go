package deploy

import (
	"context"
	"errors"
	"io"
	"math"
	"os"
	"path/filepath"
	"reflect"
	"sort"

	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

type Validator interface {
	Validate(context.Context, *upload.Envelope, string) (release.Report, error)
}

type Generation struct {
	Name, Path string
	Report     release.Report
	Assets     []store.ReleaseAsset
}

func (disk *Disk) PublishGeneration(ctx context.Context, source *upload.Envelope, releaseID string, expected release.Report, validator Validator) (Generation, bool, error) {
	if validator == nil || !expected.Verified {
		return Generation{}, false, errors.New("generation validator is required")
	}
	name, cleanup, err := disk.stage(ctx, source, releaseID)
	if err != nil {
		return Generation{}, false, err
	}
	path, _ := disk.GenerationPath(name)
	stagedPath := filepath.Join(disk.generations, "."+name+".tmp")
	envelope, err := generationEnvelope(stagedPath)
	if err != nil {
		cleanup()
		return Generation{}, false, err
	}
	report, err := validator.Validate(ctx, envelope, "")
	if err != nil || !reflect.DeepEqual(report, expected) {
		cleanup()
		if err != nil {
			return Generation{}, false, err
		}
		return Generation{}, false, errors.New("generation validation report changed")
	}
	assets := make([]store.ReleaseAsset, 0, len(report.Files))
	for _, file := range report.Files {
		assets = append(assets, store.ReleaseAsset{FileName: file.Name, SizeBytes: file.Size, SHA256: file.SHA256})
	}
	switched, err := disk.commit(name, cleanup)
	return Generation{Name: name, Path: path, Report: report, Assets: assets}, switched, err
}

func (disk *Disk) RecoverGeneration(ctx context.Context, validator Validator) (Generation, error) {
	name, err := disk.StableTarget()
	if err != nil {
		return Generation{}, err
	}
	path, _ := disk.GenerationPath(name)
	envelope, err := generationEnvelope(path)
	if err != nil {
		return Generation{}, err
	}
	report, err := validator.Validate(ctx, envelope, "")
	if err != nil {
		return Generation{}, err
	}
	assets := make([]store.ReleaseAsset, 0, len(report.Files))
	for _, file := range report.Files {
		assets = append(assets, store.ReleaseAsset{FileName: file.Name, SizeBytes: file.Size, SHA256: file.SHA256})
	}
	return Generation{Name: name, Path: path, Report: report, Assets: assets}, nil
}

func generationEnvelope(root string) (*upload.Envelope, error) {
	info, err := os.Lstat(root)
	if err != nil || validateImmutableGeneration(info, true) != nil {
		return nil, errors.New("generation root is unsafe")
	}
	handle, err := os.Open(root)
	if err != nil {
		return nil, err
	}
	entries, readErr := handle.ReadDir(maxGenerationEntries + 1)
	closeErr := handle.Close()
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return nil, readErr
	}
	if closeErr != nil {
		return nil, closeErr
	}
	if len(entries) == 0 || len(entries) > maxGenerationEntries {
		return nil, errors.New("generation closure is invalid")
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Name() < entries[j].Name() })
	result := make([]upload.Entry, 0, len(entries))
	var expanded int64
	for _, entry := range entries {
		path := filepath.Join(root, entry.Name())
		child, err := os.Lstat(path)
		if err != nil || validateImmutableGeneration(child, false) != nil || child.Size() < 0 || child.Size() > math.MaxInt64-expanded {
			return nil, errors.New("generation member is unsafe")
		}
		expanded += child.Size()
		result = append(result, upload.Entry{Name: entry.Name(), Path: path, Size: child.Size()})
	}
	return &upload.Envelope{ExtractionRoot: root, ExpandedBytes: expanded, Entries: result}, nil
}
