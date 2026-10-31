// One byte formatter for every WSGM Steam page that shows a size or a download amount.

/**
 * Formats a byte count exactly as the Overlay's LibraryCardView.FormatBytes does, so a size or
 * download amount reads the same on both surfaces. Units are binary: from one gibibyte (1024^3
 * bytes) up it is gigabytes rounded to one decimal with a trailing ".0" dropped, and below that it
 * is whole megabytes (1024^2 bytes), rounded to the nearest.
 * @param bytes The non-negative byte count.
 * @returns The count with its unit, such as "512 MB", "1 GB" or "14.6 GB".
 */
function wsgmFormatBytes(bytes: number) {
  const gigabyte = 1024 * 1024 * 1024;
  return bytes >= gigabyte
    ? `${(bytes / gigabyte).toFixed(1).replace(/\.0$/, "")} GB`
    : `${Math.round(bytes / (1024 * 1024))} MB`;
}
