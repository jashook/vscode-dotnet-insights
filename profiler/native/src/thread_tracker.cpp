////////////////////////////////////////////////////////////////////////////////
// Module: thread_tracker.cpp
//
////////////////////////////////////////////////////////////////////////////////


////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#include "../inc/thread_tracker.hpp"
#include <algorithm>
#include <chrono>

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////


////////////////////////////////////////////////////////////////////////////////
// thread_snapshot
////////////////////////////////////////////////////////////////////////////////
        ev31::thread_snapshot::thread_snapshot()
        {
            this->start_time = time(NULL);
            this->end_time = time(NULL);
        }

        ev31::thread_snapshot::thread_snapshot(time_t start_time)
        {
            this->involved_threads = std::set<std::pair<int, std::string> >();
            this->start_time = start_time;
            this->end_time = time(NULL);
        }

        ev31::thread_snapshot::~thread_snapshot()
        {

        }

        void ev31::thread_snapshot::add_involved_thread(int thread_id, const std::string& method_name) 
        {
            involved_threads.insert(std::make_pair(thread_id, method_name));
        }

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////



////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// thread_tracker

////////////////////////////////////////////////////////////////////////////////
// Ctor / Dtor
////////////////////////////////////////////////////////////////////////////////
        ev31::thread_tracker::thread_tracker() : thread_tracker(10)
        {
            
        }
        ev31::thread_tracker::thread_tracker(int snapshot_lookback)
        {
            this->snapshot_lookback = snapshot_lookback;
            this->snapshots = std::queue<thread_snapshot>();
            this->start_snapshot();
        }

        ev31::thread_tracker::~thread_tracker()
        {

        }

        void ev31::thread_tracker::start_snapshot() 
        {
            if (this->snapshots.size() == snapshot_lookback)
            {
                snapshots.pop();
            }
            snapshots.push(thread_snapshot());
        }

        const ev31::thread_snapshot ev31::thread_tracker::dump_thread_snapshot()
        {
            time_t now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
            snapshots.back().end_time = now;
            const ev31::thread_snapshot snapshot = snapshots.back();
            start_snapshot();
            return snapshot;
        }

        const ev31::thread_snapshot ev31::thread_tracker::get_latest_snapshot()
        {
            return snapshots.back();
        } 
        
        void ev31::thread_tracker::save_thread_data(int thread_id, const std::string& method_name, time_t start_time)
        {
            write_to_thread_information(thread_information, thread_id, method_name, start_time);
            snapshots.back().add_involved_thread(thread_id, method_name);
        }

        const thread_information_t ev31::thread_tracker::get_thread_information()
        {
            return thread_information;
        }

        void ev31::thread_tracker::write_to_thread_information(thread_information_t &thread_information, int thread_id, const std::string& method_name, time_t start_time, time_t end_time)
        {
            if (thread_information.find(thread_id) == thread_information.end())
            {
                thread_information[thread_id] = std::unordered_map<std::string, std::map<time_t, time_t>>();
                if (thread_information.at(thread_id).find(method_name) == thread_information.at(thread_id).end())
                {
                    thread_information[thread_id][method_name] = std::map<time_t, time_t>();
                }
            }
            thread_information[thread_id][method_name][start_time] = end_time;
        }

        void ev31::thread_tracker::write_to_thread_information(thread_information_t &thread_information, int thread_id, const std::string& method_name, time_t start_time)
        {
            write_to_thread_information(thread_information, thread_id, method_name, start_time, time(NULL));
        }

        // Implicit assumption that the thread_id and method_name are valid
        std::map<time_t, time_t> ev31::thread_tracker::get_thread_intervals(int thread_id, const std::string &method_name, time_t interval_start, time_t interval_end)
        {
            auto time_range_intersection = [&](const std::pair<time_t, time_t>& thread_interval) -> bool
            {
                return (thread_interval.second == time(nullptr)) || (thread_interval.first <= interval_end && thread_interval.second > interval_start);
            };

            std::map<time_t, time_t> intervals;
            std::map<time_t, time_t> &map = this->thread_information[thread_id][method_name];
            std::copy_if(map.begin(), map.end(), std::inserter(intervals, intervals.begin()), time_range_intersection);
            return intervals;
        }

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
