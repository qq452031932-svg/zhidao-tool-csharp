//go:build linux

package deploy

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

const rollbackTargetID = "88888888-8888-4888-8888-888888888888"

func TestRollbackValidatesTargetAndDurablySwitchesOneRelativeStableLink(t *testing.T) {
	disk, oldGeneration, newGeneration := publishedRollbackFixture(t, nil)
	var authorized int
	rolledBack, switched, err := disk.RollbackGenerationAuthorized(context.Background(), rollbackTargetID, oldGeneration.Name, oldGeneration.Report.Version, oldGeneration.Assets, fixedValidator{report: oldGeneration.Report}, func(context.Context) error {
		authorized++
		return nil
	})
	if err != nil || !switched || authorized != 1 || !reflect.DeepEqual(rolledBack, oldGeneration) {
		t.Fatalf("generation=%+v switched=%v authorized=%d err=%v", rolledBack, switched, authorized, err)
	}
	if target, err := disk.StableTarget(); err != nil || target != oldGeneration.Name {
		t.Fatalf("target=%q err=%v; previous=%q", target, err, newGeneration.Name)
	}
}

func TestRollbackFaultBeforeStableReplacementKeepsCurrentAndAfterReplacementRequiresRecovery(t *testing.T) {
	for _, test := range []struct {
		boundary Boundary
		switched bool
		wantOld  bool
	}{
		{boundary: BoundaryRollbackStable, switched: true, wantOld: true},
		{boundary: BoundaryRollbackParentSynced, switched: true, wantOld: true},
	} {
		t.Run(string(test.boundary), func(t *testing.T) {
			disk, oldGeneration, newGeneration := publishedRollbackFixture(t, func(boundary Boundary) error {
				if boundary == test.boundary {
					return errors.New("injected")
				}
				return nil
			})
			_, switched, err := disk.RollbackGenerationAuthorized(context.Background(), rollbackTargetID, oldGeneration.Name, oldGeneration.Report.Version, oldGeneration.Assets, fixedValidator{report: oldGeneration.Report}, func(context.Context) error { return nil })
			if err == nil || switched != test.switched {
				t.Fatalf("switched=%v err=%v", switched, err)
			}
			want := newGeneration.Name
			if test.wantOld {
				want = oldGeneration.Name
			}
			if target, targetErr := disk.StableTarget(); targetErr != nil || target != want {
				t.Fatalf("target=%q want=%q err=%v", target, want, targetErr)
			}
		})
	}
}

func TestRollbackAuthorizationIsRecheckedImmediatelyBeforeStableReplacement(t *testing.T) {
	disk, oldGeneration, newGeneration := publishedRollbackFixture(t, nil)
	_, switched, err := disk.RollbackGenerationAuthorized(context.Background(), rollbackTargetID, oldGeneration.Name, oldGeneration.Report.Version, oldGeneration.Assets, fixedValidator{report: oldGeneration.Report}, func(context.Context) error {
		return errors.New("session revoked")
	})
	if err == nil || switched {
		t.Fatalf("switched=%v err=%v", switched, err)
	}
	if target, targetErr := disk.StableTarget(); targetErr != nil || target != newGeneration.Name {
		t.Fatalf("target=%q want=%q err=%v", target, newGeneration.Name, targetErr)
	}
}

func TestRollbackRejectsAStoredTargetWhoseVersionOrAssetClosureDoesNotMatch(t *testing.T) {
	disk, oldGeneration, _ := publishedRollbackFixture(t, nil)
	if _, switched, err := disk.RollbackGenerationAuthorized(context.Background(), rollbackTargetID, oldGeneration.Name, "9.9.9", oldGeneration.Assets, fixedValidator{report: oldGeneration.Report}, func(context.Context) error { return nil }); err == nil || switched {
		t.Fatalf("version mismatch switched=%v err=%v", switched, err)
	}
	assets := append([]store.ReleaseAsset(nil), oldGeneration.Assets...)
	assets[0].SHA256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
	if _, switched, err := disk.RollbackGenerationAuthorized(context.Background(), rollbackTargetID, oldGeneration.Name, oldGeneration.Report.Version, assets, fixedValidator{report: oldGeneration.Report}, func(context.Context) error { return nil }); err == nil || switched {
		t.Fatalf("asset mismatch switched=%v err=%v", switched, err)
	}
}

func publishedRollbackFixture(t *testing.T, hook FaultHook) (*Disk, Generation, Generation) {
	t.Helper()
	disk, err := NewDisk(publicationRoot(t), hook)
	if err != nil {
		t.Fatal(err)
	}
	oldReport := release.Report{Verified: true, Version: "1.0.1", Files: []release.ReportFile{{Name: "asset.nupkg", Size: 3, SHA256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}
	oldGeneration, switched, err := disk.PublishGeneration(context.Background(), sourceEnvelope(t), testReleaseID, oldReport, fixedValidator{report: oldReport})
	if err != nil || !switched {
		t.Fatalf("old publish switched=%v err=%v", switched, err)
	}
	newReport := release.Report{Verified: true, Version: "1.0.2", Files: oldReport.Files}
	newGeneration, switched, err := disk.PublishGeneration(context.Background(), sourceEnvelope(t), rollbackTargetID, newReport, fixedValidator{report: newReport})
	if err != nil || !switched {
		t.Fatalf("new publish switched=%v err=%v", switched, err)
	}
	return disk, oldGeneration, newGeneration
}
