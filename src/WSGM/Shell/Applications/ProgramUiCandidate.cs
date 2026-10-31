using System;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>A user-visible window choice bound to its captured image and process birth time.</summary>
internal sealed record ProgramUiCandidate(AppWindow Window, string Executable, DateTime Started);
