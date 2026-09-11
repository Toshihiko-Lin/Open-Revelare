namespace OpenRevelare.ColorManagement;

/// <summary>Frozen, exact ICC profiles used as colorimetric connection-space endpoints.</summary>
public static class PcsColorProfiles
{
    private static readonly Lazy<ColorProfileRef> D50XyzProfile = new(
        CreateD50Xyz,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// LittleCMS' XYZ identity profile, serialized once from the pinned 2.19.1 build with a
    /// fixed creation timestamp. Keeping exact ICC bytes makes its identity deterministic and
    /// lets PCS transforms use the normal profile-keyed cache and lease lifecycle.
    /// </summary>
    public static ColorProfileRef D50Xyz => D50XyzProfile.Value;

    private static ColorProfileRef CreateD50Xyz()
    {
        const string frozenIccBase64 =
            "AAAB5GxjbXMEQAAAYWJzdFhZWiBYWVogB+gAAQABAAAAAAAAYWNzcE1TRlQAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAPbWAAEAAAAA0y1sY21zAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFZGVzYwAAAMAAAABGY3BydAAAAQgA" +
            "AABMd3RwdAAAAVQAAAAUY2hhZAAAAWgAAAAsQTJCMAAAAZQAAABQbWx1YwAAAAAAAAAB" +
            "AAAADGVuVVMAAAAqAAAAHABYAFkAWgAgAGkAZABlAG4AdABpAHQAeQAgAGIAdQBpAGwA" +
            "dAAtAGkAbgAAbWx1YwAAAAAAAAABAAAADGVuVVMAAAAwAAAAHABOAG8AIABjAG8AcAB5" +
            "AHIAaQBnAGgAdAAsACAAdQBzAGUAIABmAHIAZQBlAGwAeVhZWiAAAAAAAAD21gABAAAA" +
            "ANMtc2YzMgAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAABtQUIg" +
            "AAAAAAMDAAAAAAAgAAAAAAAAAAAAAAAAAAAAAHBhcmEAAAAAAAAAAAABAABwYXJhAAAA" +
            "AAAAAAAAAQAAcGFyYQAAAAAAAAAAAAEAAA==";

        return ColorProfileRef.Create(
            Convert.FromBase64String(frozenIccBase64),
            "ICC D50 PCS XYZ identity",
            ProfileRole.CanonicalPresentation,
            new ProfileSource.Generated(
                "LittleCMS.cmsCreateXYZProfile",
                "2.19.1/frozen-2024-01-01"));
    }
}
