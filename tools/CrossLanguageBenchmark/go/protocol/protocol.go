// Package protocol is the cross-language benchmark's protocol for the Go CLIs: the same arguments, timing loop and JSON as FeatherQR's
// CLI (tools/CrossLanguageBenchmark/dotnet/cli/Protocol.cs), which is the reference implementation.
// See .github/docs/plans/cross-language-benchmark-plan.md ("Protocol").
package protocol

import (
	"bytes"
	"errors"
	"fmt"
	"os"
	"runtime"
	"runtime/debug"
	"strconv"
	"strings"
	"time"
)

// Args are the protocol's arguments. Ecc and Version are empty when not given.
type Args struct {
	Mode, Op, Symbology, Input string
	Ecc, Version               string
	options                    []string
}

func (a *Args) option(name string) (string, bool) {
	for i := 0; i+1 < len(a.options); i++ {
		if a.options[i] == name {
			return a.options[i+1], true
		}
	}
	return "", false
}

// Operation is one case made into a call. The input is read and converted before any timing, so the timed call does only QR work.
type Operation struct {
	// Call is the timed unit of work. Its value is folded into the checksum, so the work cannot be dropped, and the loop calls it
	// through this func value, which the compiler does not inline into the loop. It is never 0 for a call that succeeds and always 0
	// for one that fails, so the loop counts the calls that failed.
	Call func() uint64
	// Describe makes one call and returns its result as protocol JSON members: the status, then the decoded text or the encoded matrix.
	Describe func() string
}

// Failed is the status of a call that did not decode, or an encoder that refused.
const Failed = `"status":"failed"`

// Run runs the protocol for one library, whose version is read from the module path's entry in the binary's build information.
// load returns nil for an operation the library does not offer, and an error for a bad argument.
func Run(library, module string, load func(*Args) (*Operation, error)) int {
	raw := os.Args[1:]
	if len(raw) < 4 {
		return usage()
	}
	args := &Args{Mode: raw[0], Op: raw[1], Symbology: raw[2], Input: raw[3], options: raw[4:]}
	args.Ecc, _ = args.option("--ecc")
	args.Version, _ = args.option("--version")

	version, build := identity(module)
	var json strings.Builder
	fmt.Fprintf(&json, `{"protocol":1,"library":"%s","libraryVersion":"%s","runtime":"%s","build":"%s","mode":"%s"`,
		library, version, runtime.Version(), build, args.Mode)

	operation, err := load(args)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	if operation == nil {
		fmt.Print(json.String() + `,"status":"unsupported"}` + "\n")
		return 0
	}

	// Verification checks one call. A library can still fail the calls after it, for example by changing its input, so every timed call
	// that fails is counted, and the collector rejects a process with any.
	var sink, failed uint64
	switch args.Mode {
	case "run":
		described := operation.Describe()
		json.WriteString("," + described)
		// An input the library cannot handle is reported, not timed: a failing call costs what failing costs.
		if described == Failed {
			break
		}
		warmupMs := number(args, "--warmup-ms", 3000)
		batchMs := number(args, "--batch-ms", 20)
		batches := int(number(args, "--batches", 30))
		call := operation.Call

		// Warm up for the stated time and at least 3 calls. The batch size comes from the warmup's second half.
		// time.Now carries a monotonic reading, and time.Since uses it.
		warmup := time.Duration(warmupMs * float64(time.Millisecond))
		var calls, halfCalls uint64
		var halfElapsed, elapsed time.Duration
		start := time.Now()
		for {
			value := call()
			sink += value
			if value == 0 {
				failed++
			}
			calls++
			elapsed = time.Since(start)
			if halfCalls == 0 && elapsed >= warmup/2 {
				halfCalls, halfElapsed = calls, elapsed
			}
			if calls >= 3 && elapsed >= warmup {
				break
			}
		}
		perCall := elapsed.Seconds() / float64(calls)
		if calls > halfCalls {
			perCall = (elapsed - halfElapsed).Seconds() / float64(calls-halfCalls)
		}
		batchCalls := max(uint64(batchMs/1000/perCall), 1)

		samples := make([]int64, batches)
		for b := range samples {
			t0 := time.Now()
			for k := uint64(0); k < batchCalls; k++ {
				value := call()
				sink += value
				if value == 0 {
					failed++
				}
			}
			samples[b] = time.Since(t0).Nanoseconds()
		}

		fmt.Fprintf(&json, `,"warmupCalls":%d,"warmupNs":%d,"batchCalls":%d,"batchNs":[`, calls, elapsed.Nanoseconds(), batchCalls)
		for b, sample := range samples {
			if b > 0 {
				json.WriteByte(',')
			}
			json.WriteString(strconv.FormatInt(sample, 10))
		}
		fmt.Fprintf(&json, `],"failedCalls":%d`, failed)
	case "fixed":
		option, ok := args.option("--iterations")
		iterations, err := strconv.ParseUint(option, 10, 64)
		if !ok || err != nil {
			return usage()
		}
		call := operation.Call
		for k := uint64(0); k < iterations; k++ {
			value := call()
			sink += value
			if value == 0 {
				failed++
			}
		}
		fmt.Fprintf(&json, `,"status":"ok","iterations":%d,"failedCalls":%d`, iterations, failed)
	case "cold":
		json.WriteString("," + operation.Describe())
	case "noop":
		json.WriteString(`,"status":"ok"`)
	default:
		return usage()
	}

	fmt.Printf("%s,\"checksum\":\"%d\"}\n", json.String(), sink)
	return 0
}

// identity reads the library's version and how the binary was compiled from its build information: "default" for the toolchain's
// default instruction-set level, or the level it was built for.
func identity(module string) (version, build string) {
	version, build = "unknown", "default"
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return
	}
	for _, dep := range info.Deps {
		if dep.Path == module {
			version = strings.TrimPrefix(dep.Version, "v")
		}
	}
	defaults := map[string]string{"GOAMD64": "v1", "GOARM64": "v8.0"}
	for _, setting := range info.Settings {
		if fallback, ok := defaults[setting.Key]; ok && setting.Value != fallback {
			build = setting.Key + "=" + setting.Value
		}
	}
	return
}

func number(args *Args, name string, fallback float64) float64 {
	if option, ok := args.option(name); ok {
		if value, err := strconv.ParseFloat(option, 64); err == nil {
			return value
		}
	}
	return fallback
}

func usage() int {
	fmt.Fprintln(os.Stderr, "usage: <cli> <run|fixed|cold|noop> <encode|decode-matrix|decode-image> <qr|microqr|rmqr> <input> [--ecc E] [--version V] [--warmup-ms 3000 --batch-ms 20 --batches 30 | --iterations N]")
	return 2
}

// FoldText is the fold for a decode: the text's length and last byte, so it depends on the content.
func FoldText(text string) uint64 {
	if len(text) == 0 {
		return 0
	}
	return uint64(len(text))*31 + uint64(text[len(text)-1])
}

// Decoded is a decode's result: its text as UTF-8 in hex.
func Decoded(text string) string {
	return fmt.Sprintf(`"status":"ok","text":"%X"`, text)
}

// Matrix is an encode's result: rows top to bottom, 1 for a dark module, with whatever quiet zone the library returns.
func Matrix(width, height int, isDark func(row, col int) bool) string {
	var json strings.Builder
	fmt.Fprintf(&json, `"status":"ok","matrix":{"width":%d,"height":%d,"rows":[`, width, height)
	for row := 0; row < height; row++ {
		if row > 0 {
			json.WriteByte(',')
		}
		json.WriteByte('"')
		for col := 0; col < width; col++ {
			if isDark(row, col) {
				json.WriteByte('1')
			} else {
				json.WriteByte('0')
			}
		}
		json.WriteByte('"')
	}
	json.WriteString("]}")
	return json.String()
}

// ReadText reads the payload's exact bytes.
func ReadText(path string) (string, error) {
	data, err := os.ReadFile(path)
	return string(data), err
}

// Image is 8-bit grey pixels, row by row.
type Image struct {
	Pixels        []byte
	Width, Height int
}

// ReadPgm reads a binary PGM (P5, maxval 255): the corpus's one image format.
func ReadPgm(path string) (*Image, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	position := 0
	token := func() string {
		for position < len(data) {
			if data[position] == '#' {
				for position < len(data) && data[position] != '\n' {
					position++
				}
			} else if bytes.IndexByte([]byte(" \t\n\r"), data[position]) >= 0 {
				position++
			} else {
				break
			}
		}
		start := position
		for position < len(data) && bytes.IndexByte([]byte(" \t\n\r\v\f"), data[position]) < 0 {
			position++
		}
		return string(data[start:position])
	}
	if token() != "P5" {
		return nil, errors.New(path + " is not a binary PGM (P5)")
	}
	width, errWidth := strconv.Atoi(token())
	height, errHeight := strconv.Atoi(token())
	if errWidth != nil || errHeight != nil {
		return nil, errors.New(path + ": bad size")
	}
	if token() != "255" {
		return nil, errors.New(path + ": the corpus uses maxval 255")
	}
	// Exactly one whitespace byte separates the header from the raster.
	raster := data[position+1:]
	if len(raster) != width*height {
		return nil, fmt.Errorf("%s holds %d pixel bytes for %dx%d", path, len(raster), width, height)
	}
	return &Image{raster, width, height}, nil
}
