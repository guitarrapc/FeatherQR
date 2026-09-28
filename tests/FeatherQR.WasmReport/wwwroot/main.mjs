// Runs the report under Node.js: node main.mjs --simd-class Wasm. The runtime exits with Main's return value.
import { dotnet } from './_framework/dotnet.js';

await dotnet.withApplicationArguments(...process.argv.slice(2)).run();
