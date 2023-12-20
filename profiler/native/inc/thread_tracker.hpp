////////////////////////////////////////////////////////////////////////////////
// Module: thread_tracker.hpp
//
////////////////////////////////////////////////////////////////////////////////

#ifndef __THREAD_TRACKER_HPP__
#define __THREAD_TRACKER_HPP__

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#include <vector>
#include <unordered_map>
#include <string>
#include <map>
#include <queue>
#include <utility>
#include <time.h>
#include <set>

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
typedef std::unordered_map<int, std::unordered_map<std::string, std::map<time_t, time_t> > > thread_information_t;

namespace ev31 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

struct thread_snapshot 
{
    public:
        thread_snapshot();
        thread_snapshot(time_t start_time);
        ~thread_snapshot();

    public:
        void add_involved_thread(int thread_id, const std::string& method_name);
        std::set<std::pair<int, std::string> > involved_threads;
        time_t start_time;
        time_t end_time;
};



class thread_tracker
{
    // Ctor / Dtor
    public:
        thread_tracker(int snapshot_lookback);
        thread_tracker();
        ~thread_tracker();

    // Member Methods
    public:
        const thread_snapshot dump_thread_snapshot();
        void save_thread_data(int thread_id, const std::string& method_name, time_t start_time);
        const thread_information_t get_thread_information();
        std::map<time_t, time_t> get_thread_intervals(int thread_id, const std::string& method_name, time_t interval_start, time_t interval_end);


    private:
        thread_information_t thread_information;
        std::queue<thread_snapshot> snapshots;
        int snapshot_lookback;

        void start_snapshot();
        const thread_snapshot get_latest_snapshot();
        void write_to_thread_information(thread_information_t &thread_information, int thread_id, const std::string& method_name, time_t start_time);
        void write_to_thread_information(thread_information_t &thread_information, int thread_id, const std::string& method_name, time_t start_time, time_t end_time);

};
////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace (ev31)

// ////////////////////////////////////////////////////////////////////////////////
// ////////////////////////////////////////////////////////////////////////////////

#endif // __THREAD_TRACKER_HPP__

// ////////////////////////////////////////////////////////////////////////////////
// ////////////////////////////////////////////////////////////////////////////////