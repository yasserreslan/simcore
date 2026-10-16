package sim

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

// deterministicPackages must follow the determinism rules. Test files are
// exempt.
var deterministicPackages = []string{".", "../fixed", "../rng"}

// bannedImports can make results differ between runs or machines.
var bannedImports = map[string]string{
	"math":         "float math; use package fixed",
	"math/rand":    "unseeded or global randomness; use package rng",
	"math/rand/v2": "unseeded or global randomness; use package rng",
	"time":         "wall-clock time differs between machines",
	"sync":         "concurrency makes ordering nondeterministic",
	"sync/atomic":  "concurrency makes ordering nondeterministic",
	"os":           "environment differs between machines",
	"unsafe":       "memory layout differs between architectures",
	"reflect":      "not needed by the sim",
}

var bannedTypes = map[string]bool{"float32": true, "float64": true, "complex64": true, "complex128": true}

// TestDeterminismRules fails if sim code uses anything that could make two
// machines compute different results from the same commands.
func TestDeterminismRules(t *testing.T) {
	fset := token.NewFileSet()
	for _, dir := range deterministicPackages {
		files, err := filepath.Glob(filepath.Join(dir, "*.go"))
		if err != nil {
			t.Fatal(err)
		}
		for _, path := range files {
			if strings.HasSuffix(path, "_test.go") {
				continue
			}
			src, err := os.ReadFile(path)
			if err != nil {
				t.Fatal(err)
			}
			f, err := parser.ParseFile(fset, path, src, 0)
			if err != nil {
				t.Fatal(err)
			}
			checkFile(t, fset, f)
		}
	}
}

func checkFile(t *testing.T, fset *token.FileSet, f *ast.File) {
	t.Helper()
	for _, imp := range f.Imports {
		p, _ := strconv.Unquote(imp.Path.Value)
		if why, bad := bannedImports[p]; bad {
			t.Errorf("%s: imports %q: %s", fset.Position(imp.Pos()), p, why)
		}
	}
	ast.Inspect(f, func(n ast.Node) bool {
		switch n := n.(type) {
		case *ast.Ident:
			if bannedTypes[n.Name] {
				t.Errorf("%s: uses %s; the sim is integer-only", fset.Position(n.Pos()), n.Name)
			}
		case *ast.BasicLit:
			if n.Kind == token.FLOAT || n.Kind == token.IMAG {
				t.Errorf("%s: float literal %s; the sim is integer-only", fset.Position(n.Pos()), n.Value)
			}
		case *ast.MapType:
			t.Errorf("%s: map type; map iteration order is random, use a sorted slice", fset.Position(n.Pos()))
		case *ast.GoStmt:
			t.Errorf("%s: go statement; the sim runs on one goroutine", fset.Position(n.Pos()))
		case *ast.SelectStmt, *ast.ChanType:
			t.Errorf("%s: channels; the sim runs on one goroutine", fset.Position(n.Pos()))
		}
		return true
	})
}
