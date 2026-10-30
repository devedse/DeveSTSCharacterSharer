// Show a page-wide drop target while files are dragged over the window. The drop itself lands on
// a transparent Blazor <InputFile> inside .drop-overlay, which does the upload.
(() => {
    const isFileInput = (el) => el instanceof HTMLInputElement && el.type === "file";
    const isFileDrag = (e) => [...(e.dataTransfer?.types ?? [])].includes("Files");
    const stop = () => document.body.classList.remove("dragging");

    window.addEventListener("dragenter", (e) => {
        if (isFileDrag(e)) document.body.classList.add("dragging");
    });
    window.addEventListener("dragleave", (e) => {
        // The overlay covers everything, so leaving it means leaving the window (or Esc).
        if (e.target instanceof Element && e.target.closest(".drop-overlay")) stop();
    });
    window.addEventListener("dragover", (e) => {
        if (isFileDrag(e) && !isFileInput(e.target)) e.preventDefault();
    });
    window.addEventListener("drop", (e) => {
        // Entries have to be taken now; the browser empties dataTransfer once the event is over.
        const entries = [...(e.dataTransfer?.items ?? [])].filter((i) => i.kind === "file").map((i) => i.webkitGetAsEntry?.());
        if (entries.some((entry) => entry?.isDirectory)) {
            // A file input can't upload folders, so take over and hand it the packed folders.
            e.preventDefault();
            uploadEntries(entries, document.querySelector(".drop-overlay input[type=file]"));
        } else if (!isFileInput(e.target)) {
            // Anywhere but on a file input, don't let the browser open the file.
            e.preventDefault();
        }
        setTimeout(stop);
    });

    // Dropped folders become a .tar each (the server unpacks tar like any other archive), so a
    // character's folder, or a whole mods folder with several characters, can be dropped as is.
    async function uploadEntries(entries, input) {
        const files = new DataTransfer();
        for (const entry of entries.filter(Boolean)) {
            if (entry.isDirectory) {
                const contents = [];
                await collectFiles(entry, entry.name, contents);
                files.items.add(new File([tar(contents)], `${entry.name} (folder)`, { type: "application/x-tar" }));
            } else {
                files.items.add(await new Promise((resolve, reject) => entry.file(resolve, reject)));
            }
        }
        input.files = files.files;
        input.dispatchEvent(new Event("change", { bubbles: true }));
    }

    async function collectFiles(entry, path, out) {
        if (entry.isFile) {
            out.push({ path, file: await new Promise((resolve, reject) => entry.file(resolve, reject)) });
        } else if (entry.isDirectory) {
            const reader = entry.createReader();
            // readEntries returns a folder's contents in batches, until an empty one.
            for (let batch; (batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject))).length; ) {
                for (const child of batch) await collectFiles(child, `${path}/${child.name}`, out);
            }
        }
    }

    const encoder = new TextEncoder();

    // A tar Blob that refers to the dropped files instead of copying them, so even big .pck files
    // cost no memory. Names longer than 100 bytes get a GNU long name entry first.
    function tar(files) {
        const padding = (size) => (size % 512 ? [new Uint8Array(512 - (size % 512))] : []);
        const parts = [];
        for (const { path, file } of files) {
            const name = encoder.encode(path);
            if (name.length > 100) {
                parts.push(tarHeader("././@LongLink", name.length + 1, 0, "L"), name, new Uint8Array(1), ...padding(name.length + 1));
            }
            parts.push(tarHeader(path, file.size, Math.floor(file.lastModified / 1000), "0"), file, ...padding(file.size));
        }
        parts.push(new Uint8Array(1024)); // two empty blocks end the archive
        return new Blob(parts);
    }

    function tarHeader(name, size, mtime, type) {
        const header = new Uint8Array(512);
        const put = (offset, length, text) => header.set(encoder.encode(text).subarray(0, length), offset);
        const octal = (value, length) => value.toString(8).padStart(length - 1, "0");
        put(0, 100, name);
        put(100, 8, "0000644");
        put(108, 8, "0000000");
        put(116, 8, "0000000");
        put(124, 12, octal(size, 12));
        put(136, 12, octal(mtime, 12));
        put(148, 8, " ".repeat(8)); // the checksum is computed with spaces in its own place
        put(156, 1, type);
        put(257, 8, "ustar  "); // GNU magic and version
        put(148, 8, octal(header.reduce((sum, b) => sum + b, 0), 7) + "\0 ");
        return header;
    }
})();
