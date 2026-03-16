# Reconstruction Notes

- Source structure follows the deployed `VMS.exe` / `NewSanofi` namespaces discovered from PDB and metadata.
- Current project is intentionally kept close to the original architecture:
  - `App`, `MainWindow`
  - `ViewModel\BaseViewModel`, `MainViewModel`, `SubControlViewModel`, `RelayCommand<T>`
  - `UserControls\SubControl`
  - `Windows\ManualDialog`, `MessageWindow`
  - `SqliteCom`
  - `ClassHelper\*`
- The machine used to rebuild does not have the `.NET Framework 4.6.1 Developer Pack`, so the project is temporarily targeted to `.NET Framework 4.7.2`.
- To switch back to `4.6.1` later:
  - install the `.NET Framework 4.6.1 Developer Pack`
  - change `TargetFrameworkVersion` in `NewSanofi.Reconstructed.csproj`
  - change `supportedRuntime` in `App.config`
- Current implementation goal is "compileable architectural reconstruction", not a byte-for-byte decompilation.
- TCP, SQLite, file persistence, and Excel import are wired at a basic level so the project can be extended incrementally.
