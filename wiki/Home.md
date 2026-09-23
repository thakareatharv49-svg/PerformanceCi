# PerformanceCI

A professional-grade performance‑monitoring library for .NET projects, inspired by testing frameworks.  
It continuously watches your code for performance regressions and alerts you when thresholds are exceeded.

## Features
- **Automated Benchmarking** – Runs performance benchmarks on each push.  
- **Threshold Alerts** – Configurable alerts that trigger on regressions.  
- **CI Integration** – Native GitHub Actions pipeline for automated testing.  
- **Package Distribution** – Publishes to GitHub Packages for easy consumption.  
- **Extensible API** – Simple fluent API to add custom metrics.

## Getting Started
1. **Install**  
   ```bash
   dotnet add package Vok.Performance.Core
   ```
2. **Configure** – Add a `performance.yml` to define metrics and thresholds.  
3. **Run Locally** – `dotnet test` triggers CI checks.  
4. **View Results** – Dashboard available in the Actions tab on GitHub.

## API Overview
- `PerformanceMonitor.Start()` – Begin tracking.  
- `PerformanceMonitor.Stop()` – End tracking and evaluate.  
- `PerformanceMonitor.Assert()` – Assert against defined thresholds.  
- `PerformanceMonitor.Report()` – Generate a detailed HTML report.

## CI Pipeline
- **Trigger** – Every push to `main` runs the pipeline.  
- **Steps** – Restore, Build, Test, Benchmark, Publish.  
- **Badge** – ![CI](https://github.com/Divide-By-Zero-Solutions/PerformanceCI/actions/workflows/performanceCI.yml/badge.svg)

## Milestones
- **v0.1** – Initial release with core monitoring.  
- **v1.0** – Full CI pipeline, benchmark suite, alerts.  
- **v2.0** – Advanced reporting, multi‑project support.

## Issue Tracking
- Open issues: https://github.com/Divide-By-Zero-Solutions/PerformanceCI/issues  
- Label guide: `bug`, `enhancement`, `question`.

## Contributing
- Fork the repo, create a feature branch, and submit a pull request.  
- Follow the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md).  

## License
MIT License – see the [LICENSE](LICENSE) file for details.

## Links
- Documentation: https://Divide-By-Zero-Solutions.github.io/PerformanceCI  
- Package: https://nuget.org/packages/Vok.Performance.Core  
- Roadmap: https://github.com/Divide-By-Zero-Solutions/PerformanceCI/milestones