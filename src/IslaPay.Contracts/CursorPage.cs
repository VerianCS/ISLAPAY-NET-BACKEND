namespace IslaPay.Contracts;

/// <summary>
/// A page of results with an opaque cursor.
/// </summary>
/// <remarks>
/// Cursor rather than offset, per §11.1 of the architecture. Offset paging
/// skips or repeats rows when the underlying set changes between pages, and a
/// wallet history grows while the user is scrolling it.
/// <para>
/// <paramref name="NextCursor"/> is opaque: the client stores it and sends it
/// back, and must not parse or construct one. That keeps the server free to
/// change how it encodes position without breaking clients in the field.
/// </para>
/// </remarks>
/// <param name="Items">This page, in the endpoint's documented order.</param>
/// <param name="NextCursor">
/// Pass as <c>?cursor=</c> for the next page. <c>null</c> means this is the
/// last page — the only reliable end-of-list signal, since a full page can
/// still be the final one.
/// </param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor = null);
