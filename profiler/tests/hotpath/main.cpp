// function example
#include <iostream>
#include <chrono>
#include <vector>
#include "../../native/inc/thread_tracker.hpp"

int main ()
{   
    ev31::thread_tracker tracker = ev31::thread_tracker();
    std::vector<ev31::thread_snapshot> snapshots;

    auto hot_path_start_time = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < 10000; i++)
    {
        std::time_t now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
        
        for (int j = 0; j < 1000; j++) 
        {
            int thread_id = i * 10000 + j;
            tracker.save_thread_data(thread_id, std::to_string(thread_id), now);
            if (j % 100 == 0)
            {
                snapshots.push_back(tracker.dump_thread_snapshot());
            }
        }
    }
    auto hot_path_end_time = std::chrono::high_resolution_clock::now();

    std::cout << "Hot path took " << std::chrono::duration_cast<std::chrono::milliseconds>(hot_path_end_time - hot_path_start_time).count() << " milliseconds" << std::endl;

    auto snapshots_start_time = std::chrono::high_resolution_clock::now();

    for (auto &snapshot : snapshots)
    {
        auto start = snapshot.involved_threads.begin();
        tracker.get_thread_intervals(start->first, start->second, snapshot.start_time, snapshot.end_time);
    }

    auto snapshots_end_time = std::chrono::high_resolution_clock::now();
    std::cout << "Snapshots length: " << snapshots.size() << std::endl;
    std::cout << "Snapshots took " << std::chrono::duration_cast<std::chrono::milliseconds>(snapshots_end_time - snapshots_start_time).count() << " milliseconds" << std::endl;
    return 0;
}