#include "RingBuffer.h"
#include <stdexcept>

RingBuffer::RingBuffer(std::size_t capacity) : capacity_(capacity) {}

void RingBuffer::push(int value) {
    if (data_.size() < capacity_) data_.push_back(value);
}

int RingBuffer::at(std::size_t index) const {
    return data_.at(index);
}

std::size_t RingBuffer::size() const {
    return data_.size();
}
