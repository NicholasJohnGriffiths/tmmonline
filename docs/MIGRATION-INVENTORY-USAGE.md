# Legacy Site Inventory Script Usage

Script location:

- scripts/Invoke-LegacySiteInventory.ps1

## Purpose

Crawls tmmonline.nz (or another base URL), then exports:

- pages.csv: discovered pages, titles, status, and link counts
- media.csv: discovered image URLs and source pages
- summary.json: run metadata and output paths

## Run Example

```powershell
cd d:\Dev\TMMOnline
.\scripts\Invoke-LegacySiteInventory.ps1 -BaseUrl "https://tmmonline.nz" -OutputDir ".\migration-output" -MaxPages 800
```

## Notes

- The crawler follows internal links on the same host only.
- It skips mailto:, tel:, javascript:, and hash-only links.
- Increase MaxPages for a larger crawl.
- Review output and deduplicate/clean before import mapping.
