using System.Runtime.CompilerServices;

// Internal helpers (object-key construction, extension resolution) are covered directly
// by unit tests rather than widened into the public surface.
[assembly: InternalsVisibleTo("Cx.UnitTests")]
