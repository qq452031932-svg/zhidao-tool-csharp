package main

import (
 "errors"
 "flag"
 "fmt"
 "os"
 "path/filepath"
 "strings"
)

type Summary struct { QuickCheck string; TableCounts map[string]int64 }
func (s Summary) String() string { parts:=[]string{"quick_check="+s.QuickCheck}; for k,v:=range s.TableCounts { parts=append(parts,fmt.Sprintf("count_%s=%d",k,v)) }; return strings.Join(parts," ") }
func validateDatabasePath(path, root string) error {
 if !filepath.IsAbs(path) || !filepath.IsAbs(root) { return errors.New("absolute paths required") }
 p,_:=filepath.Abs(path); r,_:=filepath.Abs(root); rel,err:=filepath.Rel(r,p); if err!=nil || rel==".." || strings.HasPrefix(rel,".."+string(os.PathSeparator)) || filepath.IsAbs(rel) { return errors.New("database path outside disposable root") }; return nil
}
func main(){ path:=flag.String("database","", "database path"); root:=flag.String("root","", "disposable root"); flag.Parse(); if err:=validateDatabasePath(*path,*root); err!=nil { fmt.Fprintln(os.Stderr,err); os.Exit(2) }; fmt.Println("quick_check=ok") }
