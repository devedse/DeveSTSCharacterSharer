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
        // Let the file input handle its own drop; anywhere else, don't let the browser open the file.
        if (!isFileInput(e.target)) e.preventDefault();
        setTimeout(stop);
    });
})();
