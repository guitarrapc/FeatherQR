// The cross-language benchmark's Go CLIs, one command per library under cmd/, sharing the protocol in protocol/.
// See .github/docs/plans/cross-language-benchmark-plan.md. Each library is pinned to the version `go get` resolves for it,
// and go.sum pins every module's content.
module xlang-go

go 1.27

require (
	github.com/makiuchi-d/gozxing v0.1.1
	github.com/skip2/go-qrcode v0.0.0-20200617195104-da1b6568686e
)

require (
	golang.org/x/text v0.3.7 // indirect
	golang.org/x/xerrors v0.0.0-20200804184101-5ec99f83aff1 // indirect
)
