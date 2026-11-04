// Application-owned scheduling through LibRaw's decoder/interpolation hooks.
// Upstream strip decoding, AHD tile kernels and all pixel arithmetic remain unchanged.
#pragma once
#include <libraw/libraw.h>
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdlib>
#include <exception>
#include <memory>
#include <mutex>
#include <thread>

namespace open_revelare {

class LockedBufferStream final : public LibRaw_buffer_datastream {
    std::mutex gate;
public:
    LockedBufferStream(const void* data, size_t size) : LibRaw_buffer_datastream(data, size) {}
    int lock() override { gate.lock(); return 1; }
    void unlock() override { gate.unlock(); }
};

// Shared across files: more GUI decode jobs must not multiply the native worker budget.
class NativeWorkers {
    std::mutex gate;
    std::condition_variable ready;
    unsigned active = 0;
    const unsigned capacity = std::max(1u, std::min(8u, std::thread::hardware_concurrency() * 2 / 3));
public:
    unsigned limit() const { return capacity; }
    unsigned acquire(unsigned requested) {
        std::unique_lock<std::mutex> guard(gate);
        ready.wait(guard, [&] { return active < capacity; });
        unsigned count = std::min(requested, capacity - active);
        active += count;
        return count;
    }
    void release(unsigned count) {
        { std::lock_guard<std::mutex> guard(gate); active -= count; }
        ready.notify_all();
    }
};

inline unsigned requested_workers(const char* variable, unsigned jobs) {
    unsigned requested = std::min(4u, jobs);
    if (const char* value = std::getenv(variable)) {
        int configured = std::atoi(value);
        if (configured > 0) requested = std::min(requested, static_cast<unsigned>(configured));
    }
    return requested;
}

inline NativeWorkers& worker_budget() { static NativeWorkers workers; return workers; }

inline std::atomic<unsigned>& processing_contexts() { static std::atomic<unsigned> active{0}; return active; }

class ProcessingScope {
public:
    ProcessingScope() { processing_contexts().fetch_add(1); }
    ~ProcessingScope() { processing_contexts().fetch_sub(1); }
};

template<class Work> void parallel_jobs(int count, unsigned requested, Work work) {
    NativeWorkers& workers = worker_budget();
    const unsigned admitted = workers.acquire(requested);
    struct Reservation {
        NativeWorkers& workers;
        unsigned count;
        ~Reservation() { workers.release(count); }
    } reservation{workers, admitted};
    std::atomic<int> next{0};
    std::exception_ptr failure;
    std::mutex failure_gate;
    auto run = [&](unsigned worker) {
        try {
            for (int job = next.fetch_add(1); job < count; job = next.fetch_add(1)) work(job, worker);
        } catch (...) {
            next.store(count);
            std::lock_guard<std::mutex> guard(failure_gate);
            if (!failure) failure = std::current_exception();
        }
    };
    std::thread threads[3];
    unsigned launched = 0;
    try {
        for (; launched + 1 < admitted; ++launched) threads[launched] = std::thread(run, launched + 1);
    } catch (const std::system_error&) {
        // Resource pressure reduces fan-out; the caller still drains all remaining jobs.
    }
    run(0);
    for (unsigned i = 0; i < launched; ++i) threads[i].join();
    if (failure) std::rethrow_exception(failure);
}

class ParallelRaw final : public LibRaw {
protected:
    void fuji_decode_loop(fuji_compressed_params* common, int count, INT64* offsets,
                          unsigned* sizes, uchar* q_bases) override {
#ifdef LIBRAW_USE_OPENMP
        LibRaw::fuji_decode_loop(common, count, offsets, sizes, q_bases);
#else
        // Direct file/other custom streams retain upstream's serial path. Only the app's
        // immutable pinned input stream supplies seek/read locking for parallel strips.
        if (count < 2 || !dynamic_cast<LockedBufferStream*>(libraw_internal_data.internal_data.input)) {
            LibRaw::fuji_decode_loop(common, count, offsets, sizes, q_bases);
            return;
        }
        const int line_step = (libraw_internal_data.unpacker_data.fuji_total_lines + 15) & ~15;
        parallel_jobs(count, requested_workers("OPENREVELARE_RAW_UNPACK_THREADS", count), [&](int block, unsigned) {
            fuji_decode_strip(common, block, offsets[block], sizes[block],
                              q_bases ? q_bases + block * line_step : nullptr);
        });
#endif
    }

    void parallel_ahd() {
        // pre_interpolate() can merge the second green and rewrite the CFA. Select
        // AHD from that processed layout, exactly where upstream chooses its demosaic.
        unsigned filters = imgdata.idata.filters;
        int real_colors = imgdata.idata.colors;
        bool bad_bayer = false;
        auto color = [filters](int row, int col) {
            return (filters >> ((((row << 1) & 14) + (col & 1)) << 1)) & 3;
        };
        for (int row = 0; row < 4; ++row)
            for (int col = 0; col < 8; ++col) {
                real_colors = std::max(real_colors, static_cast<int>(color(row, col) + 1));
                bad_bayer |= color(row, col) == color(row + 1, col) || color(row, col) == color(row, col + 1);
            }
        if (imgdata.idata.colors > 3 || real_colors > 3 || bad_bayer) { vng_interpolate(); return; }
        const int height = imgdata.sizes.height, width = imgdata.sizes.width;
        const int step = LIBRAW_AHD_TILE - 6;
        const int rows = std::max(0, (height - 7 + step - 1) / step);
        unsigned requested = requested_workers("OPENREVELARE_RAW_PROCESS_THREADS", rows);
        // Balance against all processing contexts, including the preview pass of a
        // combined preview/full decode, so early AHD jobs do not monopolize the budget.
        unsigned active = std::max(1u, processing_contexts().load());
        requested = std::min(requested, std::max(1u, worker_budget().limit() / active));
        // Preserve upstream callback/cancellation behavior and avoid thread startup on small ROIs.
        if (rows < 2 || requested < 2 || callbacks.progress_cb) { ahd_interpolate(); return; }
        cielab(nullptr, nullptr);
        border_interpolate(5);
        std::unique_ptr<char[]> buffers[4];
        const size_t buffer_size = 26 * LIBRAW_AHD_TILE * LIBRAW_AHD_TILE;
        for (unsigned i = 0; i < requested; ++i) buffers[i].reset(new char[buffer_size]());
        parallel_jobs(rows, requested, [&](int row, unsigned worker) {
            char* buffer = buffers[worker].get();
            auto rgb = reinterpret_cast<ushort (*)[LIBRAW_AHD_TILE][LIBRAW_AHD_TILE][3]>(buffer);
            auto lab = reinterpret_cast<short (*)[LIBRAW_AHD_TILE][LIBRAW_AHD_TILE][3]>(
                buffer + 12 * LIBRAW_AHD_TILE * LIBRAW_AHD_TILE);
            auto homo = reinterpret_cast<char (*)[LIBRAW_AHD_TILE][2]>(
                buffer + 24 * LIBRAW_AHD_TILE * LIBRAW_AHD_TILE);
            int top = 2 + row * step;
            for (int left = 2; left < width - 5; left += step) {
                ahd_interpolate_green_h_and_v(top, left, rgb);
                ahd_interpolate_r_and_b_and_convert_to_cielab(top, left, rgb, lab);
                ahd_interpolate_build_homogeneity_map(top, left, lab, homo);
                ahd_interpolate_combine_homogeneous_pixels(top, left, rgb, homo);
            }
        });
    }
public:
    void configure_parallel_ahd(bool half) {
        callbacks.interpolate_bayer_cb = !half && imgdata.rawdata.iparams.filters > 1000
            ? +[](void* raw) { static_cast<ParallelRaw*>(raw)->parallel_ahd(); } : nullptr;
    }

    int open_locked_buffer(const void* data, size_t size) {
        if (!data || data == reinterpret_cast<const void*>(-1)) return LIBRAW_IO_ERROR;
        // Match LibRaw::open_buffer's size and ownership rules.
        if (size > LIBRAW_MAX_DNG_RAW_FILE_SIZE && size > LIBRAW_MAX_NONDNG_RAW_FILE_SIZE
            && size > LIBRAW_MAX_CR3_RAW_FILE_SIZE) return LIBRAW_TOO_BIG;
        try {
            std::unique_ptr<LockedBufferStream> stream(new LockedBufferStream(data, size));
            if (!stream->valid()) return LIBRAW_IO_ERROR;
            libraw_internal_data.internal_data.input_internal = 0;
            int result = open_datastream(stream.get());
            if (result == LIBRAW_SUCCESS) {
                libraw_internal_data.internal_data.input_internal = 1;
                stream.release(); // recycle() owns the stream from here.
            } else {
                libraw_internal_data.internal_data.input_internal = 0;
            }
            return result;
        } catch (const std::bad_alloc&) {
            recycle();
            return LIBRAW_UNSUFFICIENT_MEMORY;
        }
    }
};
}
