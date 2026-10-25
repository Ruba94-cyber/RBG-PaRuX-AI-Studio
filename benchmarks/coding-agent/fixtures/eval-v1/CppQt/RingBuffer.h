#pragma once
#include <cstddef>
#include <vector>

class RingBuffer {
public:
    explicit RingBuffer(std::size_t capacity);
    void push(int value);
    int at(std::size_t index) const;
    std::size_t size() const;
private:
    std::vector<int> data_;
    std::size_t capacity_;
};
