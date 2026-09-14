using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace OpenRevelare.ColorManagement;

/// <summary>
/// Application-owned LittleCMS 2.19.1 engine. Native transforms are cached per managed thread;
/// a lease may execute only on the thread that acquired it, so mutable native handles are never
/// shared concurrently.
/// </summary>
public sealed class LittleCmsEngine : IColorManagementEngine
{
    public const string RequiredRelease = "2.19.1";
    public const CmmTransformFlags FixedTransformFlags =
        CmmTransformFlags.NoCache | CmmTransformFlags.NoOptimize;

    /// <summary>
    /// Flags for a <see cref="TransformPrecision.Preview"/> transform: optimisation and the
    /// one-pixel cache are on, and the precalculated CLUT (when LittleCMS chooses one) uses its
    /// high-resolution grid. See <see cref="TransformPrecision"/> for what that costs.
    /// </summary>
    public const CmmTransformFlags PreviewTransformFlags = CmmTransformFlags.HighResPrecalc;

    /// <summary>Native LittleCMS TYPE_RGB_16: interleaved R, G, B, one unsigned 16-bit integer
    /// each — the domain a <see cref="TransformPrecision.Preview"/> transform runs in.</summary>
    private const uint NativeRgb16 = (4u << 16) | (3u << 3) | 2u;

    private readonly object _gate = new();
    private readonly ThreadLocal<ThreadCache> _threadCaches;
    private readonly ManualResetEventSlim _noActiveOperations = new(initialState: true);
    private readonly ManualResetEventSlim _disposeCompleted = new(initialState: false);

    private bool _disposing;
    private bool _disposed;
    private int _activeOperations;

    private long _profileValidations;
    private long _profileValidationFailures;
    private long _leaseRequests;
    private long _cacheHits;
    private long _cacheMisses;
    private long _transformsCreated;
    private long _transformCalls;
    private long _pixelsTransformed;
    private long _nativeErrors;

    public CmmBuildIdentity Build { get; }

    public LittleCmsEngine() : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>
    /// Creates an engine from a self-contained application output directory. The overload exists
    /// for bundle verification and deployment diagnostics; normal callers use the parameterless
    /// constructor, which always resolves <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public LittleCmsEngine(string applicationDirectory)
    {
        VerifiedLittleCmsBundle bundle = LittleCmsBundle.Verify(applicationDirectory);
        LittleCmsNative.Configure(bundle.NativePath);

        int encodedVersion;
        try
        {
            encodedVersion = LittleCmsNative.cmsGetEncodedCMMversion();
        }
        catch (DllNotFoundException ex)
        {
            throw new ColorManagementException(
                $"LittleCMS {RequiredRelease} is unavailable. The application must package the native " +
                $"library under the logical name '{LittleCmsNative.LibraryName}'.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new ColorManagementException(
                $"The library loaded as '{LittleCmsNative.LibraryName}' does not expose the LittleCMS 2 API.", ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new ColorManagementException(
                $"The library loaded as '{LittleCmsNative.LibraryName}' has the wrong architecture.", ex);
        }

        if (encodedVersion != LittleCmsNative.RequiredEncodedVersion)
        {
            throw new ColorManagementException(
                $"Unsupported LittleCMS native version {FormatEncodedVersion(encodedVersion)} " +
                $"(encoded {encodedVersion}); OpenRevelare requires {RequiredRelease} " +
                $"(encoded {LittleCmsNative.RequiredEncodedVersion}).");
        }

        Build = new CmmBuildIdentity(
            Product: "LittleCMS",
            RequiredRelease: RequiredRelease,
            EncodedNativeVersion: encodedVersion,
            ReportedNativeVersion: FormatEncodedVersion(encodedVersion),
            LogicalLibraryName: LittleCmsNative.LibraryName,
            FixedFlags: FixedTransformFlags,
            NativeSha256: bundle.NativeSha256,
            SourceSha256: bundle.SourceSha256,
            BuildOptions: bundle.BuildOptions,
            NativePath: bundle.NativePath);

        _threadCaches = new ThreadLocal<ThreadCache>(
            () => new ThreadCache(this),
            trackAllValues: true);
    }

    public ProfileValidationResult Validate(ColorProfileRef profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Interlocked.Increment(ref _profileValidations);

        ThreadCache cache = BeginOperation();
        try
        {
            ProfileValidationResult result = cache.Validate(profile);
            if (!result.IsValid) Interlocked.Increment(ref _profileValidationFailures);
            return result;
        }
        finally
        {
            EndOperation(cache);
        }
    }

    public IColorTransformLease Lease(ColorTransformRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _leaseRequests);

        ThreadCache cache = BeginOperation();
        bool operationTransferredToLease = false;
        try
        {
            CmmTransformFlags effectiveFlags = request.Precision == TransformPrecision.Preview
                ? PreviewTransformFlags
                : FixedTransformFlags;
            if (request.BlackPointCompensation)
                effectiveFlags |= CmmTransformFlags.BlackPointCompensation;

            ColorTransformKey key = ColorTransformKey.From(request, Build, effectiveFlags);
            TransformEntry entry = cache.GetOrCreate(request, key, effectiveFlags, out bool created);
            if (created)
            {
                Interlocked.Increment(ref _cacheMisses);
                Interlocked.Increment(ref _transformsCreated);
            }
            else
            {
                Interlocked.Increment(ref _cacheHits);
            }

            if (!entry.TryAcquire())
            {
                throw new InvalidOperationException(
                    "The cached transform already has an active lease on this thread. Dispose the " +
                    "current lease before acquiring the same transform again.");
            }

            operationTransferredToLease = true;
            return new TransformLease(this, cache, entry, key, request);
        }
        finally
        {
            if (!operationTransferredToLease) EndOperation(cache);
        }
    }

    public CmmDiagnosticsSnapshot GetDiagnostics()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            IList<ThreadCache> caches = _threadCaches.Values;
            return new CmmDiagnosticsSnapshot(
                Build,
                Interlocked.Read(ref _profileValidations),
                Interlocked.Read(ref _profileValidationFailures),
                Interlocked.Read(ref _leaseRequests),
                Interlocked.Read(ref _cacheHits),
                Interlocked.Read(ref _cacheMisses),
                Interlocked.Read(ref _transformsCreated),
                Interlocked.Read(ref _transformCalls),
                Interlocked.Read(ref _pixelsTransformed),
                Interlocked.Read(ref _nativeErrors),
                caches.Count,
                caches.Sum(cache => cache.TransformCount),
                _activeOperations);
        }
    }

    public void Dispose()
    {
        bool waitForAnotherDisposer;
        lock (_gate)
        {
            if (_disposed) return;
            waitForAnotherDisposer = _disposing;
            if (!waitForAnotherDisposer)
            {
                ThreadCache? current = _threadCaches.IsValueCreated ? _threadCaches.Value : null;
                if (current is { ActiveOperations: > 0 })
                {
                    throw new InvalidOperationException(
                        "LittleCmsEngine.Dispose cannot run on a thread that currently owns a CMM " +
                        "operation or transform lease. Dispose the lease first.");
                }
                _disposing = true;
            }
        }

        if (waitForAnotherDisposer)
        {
            _disposeCompleted.Wait();
            return;
        }

        try
        {
            // New operations are blocked by _disposing. Existing leases retain their handles until
            // their deterministic Dispose calls EndOperation, at which point every cache is idle.
            _noActiveOperations.Wait();
            foreach (ThreadCache cache in _threadCaches.Values) cache.Dispose();
            _threadCaches.Dispose();
            _noActiveOperations.Dispose();
        }
        finally
        {
            lock (_gate)
            {
                _disposed = true;
                _disposing = false;
            }
            _disposeCompleted.Set();
        }
    }

    private ThreadCache BeginOperation()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            ThreadCache cache = _threadCaches.Value
                ?? throw new ColorManagementException("Failed to create a thread-local LittleCMS context.");
            cache.ActiveOperations++;
            _activeOperations++;
            _noActiveOperations.Reset();
            return cache;
        }
    }

    private void EndOperation(ThreadCache cache)
    {
        lock (_gate)
        {
            if (cache.ActiveOperations <= 0 || _activeOperations <= 0)
                throw new InvalidOperationException("LittleCMS operation accounting underflow.");
            cache.ActiveOperations--;
            _activeOperations--;
            if (_activeOperations == 0) _noActiveOperations.Set();
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LittleCmsEngine));
        if (_disposing) throw new ObjectDisposedException(nameof(LittleCmsEngine), "The engine is being disposed.");
    }

    private static string FormatEncodedVersion(int encoded)
    {
        if (encoded < 0) return encoded.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int major = encoded / 1000;
        int minor = (encoded / 10) % 100;
        int patch = encoded % 10;
        return patch == 0 ? $"{major}.{minor}" : $"{major}.{minor}.{patch}";
    }

    private void RecordNativeError() => Interlocked.Increment(ref _nativeErrors);

    private void RecordTransform(int pixelCount)
    {
        Interlocked.Increment(ref _transformCalls);
        Interlocked.Add(ref _pixelsTransformed, pixelCount);
    }

    private sealed class TransformLease : IColorTransformLease
    {
        private readonly LittleCmsEngine _engine;
        private readonly ThreadCache _cache;
        private readonly TransformEntry _entry;
        private readonly ColorTransformRequest _request;
        private readonly int _ownerThreadId;
        private int _disposed;

        public ColorTransformKey Key { get; }

        public TransformLease(
            LittleCmsEngine engine,
            ThreadCache cache,
            TransformEntry entry,
            ColorTransformKey key,
            ColorTransformRequest request)
        {
            _engine = engine;
            _cache = cache;
            _entry = entry;
            _request = request;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            Key = key;
        }

        public unsafe void Apply(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                throw new InvalidOperationException(
                    $"A LittleCMS transform lease is thread-affine: acquired on managed thread " +
                    $"{_ownerThreadId}, used on {Environment.CurrentManagedThreadId}.");
            }
            if (pixelCount < 0) throw new ArgumentOutOfRangeException(nameof(pixelCount));

            int required = checked(pixelCount * 3);
            if (source.Length < required)
                throw new ArgumentException($"Source contains {source.Length} floats; {required} are required.", nameof(source));
            if (destination.Length < required)
                throw new ArgumentException($"Destination contains {destination.Length} floats; {required} are required.", nameof(destination));
            if (pixelCount == 0) return;
            if (source.Overlaps(destination, out int offset) && offset != 0)
                throw new ArgumentException("Source and destination may be identical but may not partially overlap.", nameof(destination));

            _cache.Errors.Clear();
            if (_entry.SixteenBit)
            {
                ApplySixteenBit(source, destination, pixelCount);
            }
            else
            {
                fixed (float* input = source)
                fixed (float* output = destination)
                {
                    LittleCmsNative.cmsDoTransform(
                        _entry.Handle.DangerousGetHandle(), input, output, checked((uint)pixelCount));
                }
            }

            if (_cache.Errors.Last is { } error)
            {
                throw new ColorManagementException(
                    $"LittleCMS transform failed ({_request.Source} -> {_request.Destination}, " +
                    $"intent={_request.Intent}, flags={Key.EffectiveFlags}): {error}");
            }
            _engine.RecordTransform(pixelCount);
        }

        /// <summary>
        /// The preview-precision path: the float samples are the file's codes scaled to [0,1],
        /// so rounding them to 16-bit codes is exact for 8- and 16-bit sources; the native
        /// transform runs 16-bit to 16-bit (where LittleCMS's optimisations apply), and the
        /// result comes back scaled to [0,1] — quantised to 1/65535 and clamped, which is the
        /// documented cost of <see cref="TransformPrecision.Preview"/>.
        /// </summary>
        private unsafe void ApplySixteenBit(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
        {
            int count = pixelCount * 3;
            ushort[] codes = System.Buffers.ArrayPool<ushort>.Shared.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    float v = source[i] * 65535f + 0.5f;
                    codes[i] = v <= 0f ? (ushort)0 : v >= 65535f ? (ushort)65535 : (ushort)v;
                }
                fixed (ushort* io = codes)
                {
                    LittleCmsNative.cmsDoTransform16(
                        _entry.Handle.DangerousGetHandle(), io, io, checked((uint)pixelCount));
                }
                const float inv = 1.0f / 65535f;
                for (int i = 0; i < count; i++) destination[i] = codes[i] * inv;
            }
            finally
            {
                System.Buffers.ArrayPool<ushort>.Shared.Return(codes);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _entry.Release();
            _engine.EndOperation(_cache);
        }
    }

    private sealed class ThreadCache : IDisposable
    {
        private static readonly LittleCmsNative.LogErrorHandler ErrorHandler = OnNativeError;

        private readonly Dictionary<ColorTransformKey, TransformEntry> _transforms = new();
        private readonly GCHandle _errorStateHandle;
        private bool _disposed;

        internal LittleCmsContextHandle Context { get; }
        internal NativeErrorState Errors { get; }
        internal int ActiveOperations { get; set; }
        internal int TransformCount => _transforms.Count;

        internal ThreadCache(LittleCmsEngine engine)
        {
            Errors = new NativeErrorState(engine.RecordNativeError);
            _errorStateHandle = GCHandle.Alloc(Errors, GCHandleType.Normal);

            IntPtr context = IntPtr.Zero;
            try
            {
                context = LittleCmsNative.cmsCreateContext(
                    IntPtr.Zero, GCHandle.ToIntPtr(_errorStateHandle));
                if (context == IntPtr.Zero)
                    throw new ColorManagementException("LittleCMS could not create an isolated context.");

                Context = new LittleCmsContextHandle(context);
                LittleCmsNative.cmsSetLogErrorHandlerTHR(context, ErrorHandler);
            }
            catch
            {
                if (context != IntPtr.Zero) LittleCmsNative.cmsDeleteContext(context);
                _errorStateHandle.Free();
                throw;
            }
        }

        internal ProfileValidationResult Validate(ColorProfileRef profile)
        {
            if (!TryValidateHeader(profile, out string? headerFailure))
                return Invalid(profile, headerFailure!, nativeError: null);

            Errors.Clear();
            IntPtr nativeProfile = IntPtr.Zero;
            unsafe
            {
                ReadOnlySpan<byte> bytes = profile.IccBytes.AsSpan();
                fixed (byte* pointer = bytes)
                {
                    try
                    {
                        nativeProfile = LittleCmsNative.cmsOpenProfileFromMemTHR(
                            Context.DangerousGetHandle(), pointer, checked((uint)bytes.Length));
                        if (nativeProfile == IntPtr.Zero)
                            return Invalid(profile, "LittleCMS rejected the ICC payload.", Errors.Last);

                        uint colorSpace = LittleCmsNative.cmsGetColorSpace(nativeProfile);
                        uint pcs = LittleCmsNative.cmsGetPCS(nativeProfile);
                        uint version = LittleCmsNative.cmsGetEncodedICCversion(nativeProfile);
                        int channels = LittleCmsNative.cmsChannelsOfColorSpace(colorSpace);

                        if (colorSpace is not (LittleCmsNative.RgbSignature or LittleCmsNative.XyzSignature) ||
                            channels != 3)
                        {
                            return new ProfileValidationResult(
                                false, profile.Identity, profile.Description, colorSpace, pcs, version,
                                Errors.Last,
                                $"Only three-channel RGB or XYZ ICC profiles are supported; profile signature is " +
                                $"{Signature(colorSpace)} with {channels} channel(s).");
                        }
                        if (pcs is not (LittleCmsNative.XyzSignature or LittleCmsNative.LabSignature))
                        {
                            return new ProfileValidationResult(
                                false, profile.Identity, profile.Description, colorSpace, pcs, version,
                                Errors.Last,
                                $"ICC PCS must be XYZ or Lab; profile PCS is {Signature(pcs)}.");
                        }

                        return new ProfileValidationResult(
                            true, profile.Identity, profile.Description, colorSpace, pcs, version,
                            Errors.Last,
                            $"Valid three-channel {(colorSpace == LittleCmsNative.RgbSignature ? "RGB" : "XYZ")} ICC profile.");
                    }
                    finally
                    {
                        if (nativeProfile != IntPtr.Zero) LittleCmsNative.cmsCloseProfile(nativeProfile);
                    }
                }
            }
        }

        internal TransformEntry GetOrCreate(
            ColorTransformRequest request,
            ColorTransformKey key,
            CmmTransformFlags effectiveFlags,
            out bool created)
        {
            if (_transforms.TryGetValue(key, out TransformEntry? existing))
            {
                created = false;
                return existing;
            }

            TransformEntry entry = CreateTransform(request, effectiveFlags);
            _transforms.Add(key, entry);
            created = true;
            return entry;
        }

        private TransformEntry CreateTransform(
            ColorTransformRequest request,
            CmmTransformFlags effectiveFlags)
        {
            Errors.Clear();
            IntPtr sourceProfile = IntPtr.Zero;
            IntPtr destinationProfile = IntPtr.Zero;

            unsafe
            {
                ReadOnlySpan<byte> sourceBytes = request.Source.IccBytes.AsSpan();
                ReadOnlySpan<byte> destinationBytes = request.Destination.IccBytes.AsSpan();
                fixed (byte* sourcePointer = sourceBytes)
                fixed (byte* destinationPointer = destinationBytes)
                {
                    try
                    {
                        sourceProfile = LittleCmsNative.cmsOpenProfileFromMemTHR(
                            Context.DangerousGetHandle(), sourcePointer, checked((uint)sourceBytes.Length));
                        if (sourceProfile == IntPtr.Zero)
                            throw TransformFailure(request, effectiveFlags, "source profile could not be opened");

                        destinationProfile = LittleCmsNative.cmsOpenProfileFromMemTHR(
                            Context.DangerousGetHandle(), destinationPointer, checked((uint)destinationBytes.Length));
                        if (destinationProfile == IntPtr.Zero)
                            throw TransformFailure(request, effectiveFlags, "destination profile could not be opened");

                        EnsureProfileMatchesFormat(
                            sourceProfile,
                            request.Source,
                            request.SourceFormat,
                            "source");
                        EnsureProfileMatchesFormat(
                            destinationProfile,
                            request.Destination,
                            request.DestinationFormat,
                            "destination");
                        LittleCmsNative.cmsSetAdaptationStateTHR(
                            Context.DangerousGetHandle(), request.AdaptationState);

                        // A preview transform is built in LittleCMS's 16-bit domain: that is
                        // where its optimisations apply (none of them touch a float pipeline),
                        // and the lease converts the caller's floats around it.
                        bool sixteenBit = request.Precision == TransformPrecision.Preview;
                        IntPtr transform = LittleCmsNative.cmsCreateTransformTHR(
                            Context.DangerousGetHandle(),
                            sourceProfile,
                            sixteenBit ? NativeRgb16 : (uint)request.SourceFormat,
                            destinationProfile,
                            sixteenBit ? NativeRgb16 : (uint)request.DestinationFormat,
                            (uint)request.Intent,
                            (uint)effectiveFlags);
                        if (transform == IntPtr.Zero)
                            throw TransformFailure(request, effectiveFlags, "native transform creation returned null");

                        return new TransformEntry(new LittleCmsTransformHandle(transform), sixteenBit);
                    }
                    finally
                    {
                        if (destinationProfile != IntPtr.Zero)
                            LittleCmsNative.cmsCloseProfile(destinationProfile);
                        if (sourceProfile != IntPtr.Zero)
                            LittleCmsNative.cmsCloseProfile(sourceProfile);
                    }
                }
            }
        }

        private void EnsureProfileMatchesFormat(
            IntPtr handle,
            ColorProfileRef profile,
            PixelFormatDescriptor format,
            string side)
        {
            uint colorSpace = LittleCmsNative.cmsGetColorSpace(handle);
            int channels = LittleCmsNative.cmsChannelsOfColorSpace(colorSpace);
            uint expectedColorSpace = format switch
            {
                PixelFormatDescriptor.RgbFloat32 => LittleCmsNative.RgbSignature,
                PixelFormatDescriptor.XyzFloat32 => LittleCmsNative.XyzSignature,
                _ => throw new NotSupportedException($"Unsupported {side} pixel format: {format}."),
            };

            if (colorSpace != expectedColorSpace || channels != 3)
            {
                throw new ColorManagementException(
                    $"Cannot use {side} profile {profile} with {format}: the pixel format requires " +
                    $"a three-channel {Signature(expectedColorSpace)} profile, got " +
                    $"{Signature(colorSpace)} / {channels} channels.");
            }
        }

        private ColorManagementException TransformFailure(
            ColorTransformRequest request,
            CmmTransformFlags flags,
            string reason)
        {
            string native = Errors.Last is { } error ? $"; {error}" : "";
            return new ColorManagementException(
                $"LittleCMS transform creation failed: {reason}; source={request.Source}; " +
                $"destination={request.Destination}; purpose={request.Purpose}; intent={request.Intent}; " +
                $"BPC={request.BlackPointCompensation}; adaptation={request.AdaptationState:R}; " +
                $"formats={request.SourceFormat}->{request.DestinationFormat}; flags={flags}{native}.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (ActiveOperations != 0)
                throw new InvalidOperationException("Cannot dispose a thread cache with active operations.");
            _disposed = true;

            foreach (TransformEntry transform in _transforms.Values) transform.Dispose();
            _transforms.Clear();
            Context.Dispose();
            if (_errorStateHandle.IsAllocated) _errorStateHandle.Free();
        }

        private static void OnNativeError(IntPtr context, uint errorCode, IntPtr text)
        {
            try
            {
                if (context == IntPtr.Zero) return;
                IntPtr userData = LittleCmsNative.cmsGetContextUserData(context);
                if (userData == IntPtr.Zero) return;
                GCHandle handle = GCHandle.FromIntPtr(userData);
                if (handle.Target is NativeErrorState state)
                {
                    state.Report(new CmmNativeError(
                        errorCode,
                        Marshal.PtrToStringUTF8(text) ?? "Unknown LittleCMS error"));
                }
            }
            catch
            {
                // Native callbacks must never unwind through LittleCMS.
            }
        }

        private static bool TryValidateHeader(ColorProfileRef profile, out string? failure)
        {
            ImmutableArray<byte> bytes = profile.IccBytes;
            if (bytes.Length < 128)
            {
                failure = $"ICC payload is truncated ({bytes.Length} bytes; at least 128 required).";
                return false;
            }
            ReadOnlySpan<byte> span = bytes.AsSpan();
            uint declaredSize = BinaryPrimitives.ReadUInt32BigEndian(span);
            if (declaredSize < 128 || declaredSize > span.Length)
            {
                failure = $"ICC header declares {declaredSize} bytes but payload contains {span.Length}.";
                return false;
            }
            if (!span.Slice(36, 4).SequenceEqual("acsp"u8))
            {
                failure = "ICC header has no 'acsp' signature at offset 36.";
                return false;
            }
            failure = null;
            return true;
        }

        private static ProfileValidationResult Invalid(
            ColorProfileRef profile,
            string message,
            CmmNativeError? nativeError) => new(
                false, profile.Identity, profile.Description, null, null, null, nativeError,
                nativeError is null ? message : $"{message} {nativeError}");

        private static string Signature(uint signature)
        {
            Span<char> chars = stackalloc char[4];
            chars[0] = Printable((byte)(signature >> 24));
            chars[1] = Printable((byte)(signature >> 16));
            chars[2] = Printable((byte)(signature >> 8));
            chars[3] = Printable((byte)signature);
            return $"'{new string(chars)}' (0x{signature:X8})";

            static char Printable(byte value) => value is >= 32 and <= 126 ? (char)value : '?';
        }
    }

    private sealed class TransformEntry : IDisposable
    {
        private int _leased;
        internal LittleCmsTransformHandle Handle { get; }

        /// <summary>True when the native transform was created 16-bit to 16-bit (preview
        /// precision) and <see cref="TransformLease.Apply"/> must convert around it.</summary>
        internal bool SixteenBit { get; }

        internal TransformEntry(LittleCmsTransformHandle handle, bool sixteenBit)
        {
            Handle = handle;
            SixteenBit = sixteenBit;
        }

        internal bool TryAcquire() => Interlocked.CompareExchange(ref _leased, 1, 0) == 0;

        internal void Release()
        {
            if (Interlocked.Exchange(ref _leased, 0) != 1)
                throw new InvalidOperationException("LittleCMS transform lease accounting underflow.");
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _leased) != 0)
                throw new InvalidOperationException("Cannot dispose a transform with an active lease.");
            Handle.Dispose();
        }
    }

    private sealed class NativeErrorState
    {
        private readonly object _gate = new();
        private readonly Action _onError;
        private CmmNativeError? _last;

        internal NativeErrorState(Action onError) => _onError = onError;

        internal CmmNativeError? Last
        {
            get { lock (_gate) return _last; }
        }

        internal void Clear()
        {
            lock (_gate) _last = null;
        }

        internal void Report(CmmNativeError error)
        {
            lock (_gate) _last = error;
            _onError();
        }
    }
}
