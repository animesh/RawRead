import { dotnet } from './_framework/dotnet.js';

const output = document.getElementById('output');
const input = document.getElementById('rawFile');

const { getAssemblyExports, getConfig, runMainAndExit } = await dotnet
  .withDiagnosticTracing(true)
  .create();

const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);

output.textContent = 'WASM runtime loaded. Select a RAW file.';

input.addEventListener('change', async () => {
  const file = input.files?.[0];
  if (!file) return;

  output.textContent = `Reading ${file.name} (${file.size.toLocaleString()} bytes)...`;

  try {
    const buffer = await file.arrayBuffer();
    const bytes = new Uint8Array(buffer);

    output.textContent = `Passing ${file.name} to .NET...`;

    // byte[] interop is represented by Uint8Array in JavaScript.
    const result = exports.RawProbe.OpenRaw(bytes, file.name);
    output.textContent = result;
  } catch (e) {
    console.error(e);
    output.textContent = `JavaScript error:\n${e?.stack ?? e}`;
  }
});

// Keep the .NET main entry point available for normal WASM startup.
// The probe itself is invoked through the exported RawProbe.OpenRaw method.
await runMainAndExit(config.mainAssemblyName, []);
