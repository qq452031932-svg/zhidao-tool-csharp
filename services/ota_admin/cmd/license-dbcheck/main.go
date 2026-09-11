package main

import (
 "errors"
 "context"
 "database/sql"
 "flag"
 "fmt"
 "os"
 "path/filepath"
 "strings"
 _ "modernc.org/sqlite"
)

type Summary struct { QuickCheck string; TableCounts map[string]int64 }
func (s Summary) String() string { parts:=[]string{"quick_check="+s.QuickCheck}; for k,v:=range s.TableCounts { parts=append(parts,fmt.Sprintf("count_%s=%d",k,v)) }; return strings.Join(parts," ") }
func validateDatabasePath(path, root string) error {
 if !filepath.IsAbs(path) || !filepath.IsAbs(root) { return errors.New("absolute paths required") }
 p,_:=filepath.Abs(path); r,_:=filepath.Abs(root); rel,err:=filepath.Rel(r,p); if err!=nil || rel==".." || strings.HasPrefix(rel,".."+string(os.PathSeparator)) || filepath.IsAbs(rel) { return errors.New("database path outside disposable root") }; return nil
}
func CheckDatabase(path, root string) (Summary, error) {
	if err := validateDatabasePath(path, root); err != nil { return Summary{}, err }
	db, err := sql.Open("sqlite", "file:"+filepath.ToSlash(path)+"?mode=ro"); if err != nil { return Summary{}, err }; defer db.Close()
	ctx := context.Background(); var qc string; if err = db.QueryRowContext(ctx, "PRAGMA quick_check").Scan(&qc); err != nil { return Summary{}, err }; if !strings.EqualFold(qc,"ok") { return Summary{}, errors.New("sqlite quick_check failed") }
	var version int; if err = db.QueryRowContext(ctx,"SELECT version FROM license_schema_version WHERE singleton_id=1").Scan(&version); err != nil { return Summary{}, err }
	counts:=map[string]int64{}; rows,err:=db.QueryContext(ctx,"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"); if err!=nil{return Summary{},err}; defer rows.Close()
	for rows.Next(){ var name string; if err:=rows.Scan(&name); err!=nil{return Summary{},err}; var n int64; q:="SELECT count(*) FROM \""+strings.ReplaceAll(name,"\"","\"\"")+"\""; if err:=db.QueryRowContext(ctx,q).Scan(&n); err!=nil{return Summary{},err}; counts[name]=n }
	return Summary{QuickCheck:"ok", TableCounts:counts}, nil
}
func main(){ path:=flag.String("database","", "database path"); root:=flag.String("root","", "disposable root"); flag.Parse(); s,err:=CheckDatabase(*path,*root); if err!=nil { fmt.Fprintln(os.Stderr,err); os.Exit(2) }; fmt.Println(s.String()) }
