import Foundation

/// Plans contiguous, non-overlapping chunks using the same joint quiet-seam search as Windows.
enum TranscriptionChunker {
    static let maxChunkSeconds = 30
    static let boundarySearchSeconds = 5
    static let minChunkSeconds = 5

    private static let energyWindowSeconds = 0.1

    static func plan(samples: [Float], sampleRate: Int) -> [Range<Int>] {
        precondition(sampleRate > 0)
        let maxChunk = maxChunkSeconds * sampleRate
        guard samples.count > maxChunk else { return [0..<samples.count] }

        let searchRadius = boundarySearchSeconds * sampleRate
        let chunkCount = chunkCount(
            sampleCount: samples.count, maxChunk: maxChunk, searchRadius: searchRadius)
        let seams = placeSeams(
            samples: samples,
            chunkCount: chunkCount,
            maxChunk: maxChunk,
            minChunk: minChunkSeconds * sampleRate,
            searchRadius: searchRadius,
            window: max(1, Int(energyWindowSeconds * Double(sampleRate))))

        var spans: [Range<Int>] = []
        spans.reserveCapacity(chunkCount)
        var start = 0
        for seam in seams {
            spans.append(start..<seam)
            start = seam
        }
        spans.append(start..<samples.count)
        return spans
    }

    static func chunkCount(sampleCount: Int, maxChunk: Int, searchRadius: Int) -> Int {
        let count = Int(ceil(Double(sampleCount) / Double(maxChunk)))
        let slack = count * maxChunk - sampleCount
        return slack < 2 * searchRadius ? count + 1 : count
    }

    private static func placeSeams(
        samples: [Float],
        chunkCount: Int,
        maxChunk: Int,
        minChunk: Int,
        searchRadius: Int,
        window: Int
    ) -> [Int] {
        let length = samples.count
        let seamCount = chunkCount - 1
        let hop = max(1, window / 2)
        let evenLength = Double(length) / Double(chunkCount)

        var candidates: [[Int]] = []
        var energies: [[Double]] = []
        var evenCuts: [Int] = []
        candidates.reserveCapacity(seamCount)
        energies.reserveCapacity(seamCount)
        evenCuts.reserveCapacity(seamCount)

        for seamIndex in 0..<seamCount {
            let seam = seamIndex + 1
            let even = Int((Double(seam) * evenLength).rounded(.toNearestOrEven))
            evenCuts.append(even)

            let lo = max(
                even - searchRadius,
                max(seam * minChunk, length - (chunkCount - seam) * maxChunk))
            let hi = min(
                even + searchRadius,
                min(seam * maxChunk, length - (chunkCount - seam) * minChunk))

            let first = Int(ceil(Double(lo - even) / Double(hop)))
            let last = Int(floor(Double(hi - even) / Double(hop)))
            var grid: [Int] = []
            if first <= last {
                grid.reserveCapacity(last - first + 1)
                for k in first...last {
                    grid.append(even + k * hop)
                }
            }
            if grid.isEmpty {
                grid.append(min(max(even, lo), max(lo, hi)))
            }

            candidates.append(grid)
            energies.append(grid.map { windowEnergy(samples: samples, position: $0, window: window) })
        }

        var totalEnergy = candidates.map { Array(repeating: Double.infinity, count: $0.count) }
        var totalDistance = candidates.map { Array(repeating: Int.max, count: $0.count) }
        var previous = candidates.map { Array(repeating: -1, count: $0.count) }

        for seamIndex in 0..<seamCount {
            for candidateIndex in candidates[seamIndex].indices {
                let position = candidates[seamIndex][candidateIndex]
                let distance = abs(position - evenCuts[seamIndex])
                if seamIndex == 0 {
                    totalEnergy[seamIndex][candidateIndex] = energies[seamIndex][candidateIndex]
                    totalDistance[seamIndex][candidateIndex] = distance
                    continue
                }

                var bestEnergy = Double.infinity
                var bestDistance = Int.max
                var bestFrom = -1
                for priorIndex in candidates[seamIndex - 1].indices {
                    let chunk = position - candidates[seamIndex - 1][priorIndex]
                    guard chunk >= minChunk, chunk <= maxChunk,
                        !totalEnergy[seamIndex - 1][priorIndex].isInfinite
                    else {
                        continue
                    }

                    let energy = totalEnergy[seamIndex - 1][priorIndex]
                    let distance = totalDistance[seamIndex - 1][priorIndex]
                    if energy < bestEnergy || (energy == bestEnergy && distance < bestDistance) {
                        bestEnergy = energy
                        bestDistance = distance
                        bestFrom = priorIndex
                    }
                }

                if bestFrom >= 0 {
                    totalEnergy[seamIndex][candidateIndex] = bestEnergy + energies[seamIndex][candidateIndex]
                    totalDistance[seamIndex][candidateIndex] = bestDistance + distance
                    previous[seamIndex][candidateIndex] = bestFrom
                }
            }
        }

        let lastSeam = seamCount - 1
        var winner = -1
        for candidateIndex in candidates[lastSeam].indices {
            guard !totalEnergy[lastSeam][candidateIndex].isInfinite else { continue }
            if winner < 0
                || totalEnergy[lastSeam][candidateIndex] < totalEnergy[lastSeam][winner]
                || (totalEnergy[lastSeam][candidateIndex] == totalEnergy[lastSeam][winner]
                    && totalDistance[lastSeam][candidateIndex] < totalDistance[lastSeam][winner])
            {
                winner = candidateIndex
            }
        }
        guard winner >= 0 else { return evenCuts }

        var seams = Array(repeating: 0, count: seamCount)
        for seamIndex in stride(from: lastSeam, through: 0, by: -1) {
            seams[seamIndex] = candidates[seamIndex][winner]
            winner = previous[seamIndex][winner]
        }
        return seams
    }

    private static func windowEnergy(samples: [Float], position: Int, window: Int) -> Double {
        let start = min(max(position - window / 2, 0), max(0, samples.count - window))
        let end = min(samples.count, start + window)
        var energy = 0.0
        for index in start..<end {
            let sample = Double(samples[index])
            energy += sample * sample
        }
        return energy
    }
}
