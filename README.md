<p align="right">
🇺🇸 English | <a href="README.ko.md">🇰🇷 한국어</a>
</p>

---

# Switch Merge / Split Tool (GUI)

GUI tool for merging and splitting Nintendo Switch NSP, NSZ, XCI, and XCZ files.

This tool uses a streaming pipeline that does not create temporary files on disk.

<img width="800" height="600" alt="image" src="https://github.com/user-attachments/assets/e2564a4e-cefd-4458-a0ae-17c2feb9f332" />

---

## Features

### Merge base game, updates, and DLC into a single file

- Drag & drop support  
- Automatic format detection  
- Optional compression when merging (NSZ)

### Split merged files into individual files

- Split base game, updates, and DLC into separate files

---

## Supported Formats

- NSP (merge / split)  
- XCI (merge / split)  
- NSZ (merge / split, auto decompression)  
- XCZ (merge / split, auto decompression)

All outputs are generated as NSP or NSZ files.

---

## Automatic Decompression

When using compressed formats:

- NSZ files are automatically decompressed  
- XCZ files are automatically decompressed  

Decompression runs simultaneously during processing.

---

## Streaming Processing Pipeline

This tool uses a real-time streaming pipeline to:

- Process data during merge and split operations  
- Prevent temporary file creation  
- Reduce disk usage  
- Improve performance  

### Traditional workflow

1. Decompress NSZ/XCZ  
2. Create temporary NSP  
3. Perform merge or split  

This tool performs all steps simultaneously.

---

## Development Environment

- Visual Studio 2026  
- .NET 8.0 (LTS)  
- LibHac  
- Avalonia  

---

## Legal Notice

This project does not include any encryption keys.  
This project is intended for research and development purposes only.
