using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>A tab order the user pressed to, waiting for the writer.</summary>
/// <param name="Keys">Every tab key, left to right.</param>
/// <param name="Hidden">The native tabs left out of the strip.</param>
internal sealed record LibraryTabOrderPress(List<string> Keys, List<string> Hidden);
