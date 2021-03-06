﻿/*
 *  Licensed to SharpSoftware under one or more contributor
 *  license agreements. See the NOTICE file distributed with this work for 
 *  additional information regarding copyright ownership.
 * 
 *  SharpSoftware licenses this file to you under the Apache License, 
 *  Version 2.0 (the "License"); you may not use this file except in 
 *  compliance with the License. You may obtain a copy of the License at
 * 
 *       http://www.apache.org/licenses/LICENSE-2.0
 * 
 *  Unless required by applicable law or agreed to in writing, software
 *  distributed under the License is distributed on an "AS IS" BASIS,
 *  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *  See the License for the specific language governing permissions and
 *  limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.IO;
using Itinero.Data.Shapes;
using Itinero.Data.Tiles;
using Itinero.LocalGeo;
using Reminiscence;
using Reminiscence.Arrays;
using Reminiscence.Arrays.Sparse;

namespace Itinero.Data.Graphs
{
    public sealed class Graph
    {
        private const byte DefaultTileCapacityInBits = 0; 
        
        private readonly int _zoom; // the zoom level.
        private const int CoordinateSizeInBytes = 3; // 3 bytes = 24 bits = 4096 x 4096, the needed resolution depends on the zoom-level, higher, less resolution.
        const int TileResolutionInBits = CoordinateSizeInBytes * 8 / 2;
        private const int TileSizeInIndex = 5; // 4 bytes for the pointer, 1 for the size.
        
        // The tile-index.
        // - pointer (4 bytes): the pointer to the first vertex in the tile.
        // - capacity (in max bits, 1 byte) : the capacity in # of bytes.
        private readonly SparseMemoryArray<byte> _tiles;

        private readonly ArrayBase<byte> _vertices; // holds the vertex location, encoded relative to a tile.
        private readonly ArrayBase<uint> _edgePointers; // holds edge pointers, points to the first edge for each vertex.
        
        // the edges contain per edge at least 6 uint's
        // ideas for future encodings as follows:
        // - vertex1: the id of vertex1, we only store the localid if both vertices belong to the same tile.
        // - vertex2: the id of vertex1, we only store the localid if both vertices belong to the same tile.
        //   -> we store only the local id if both vertices belong to the same tile.
        //   -> we know this when both are < uint.maxvalue (TODO: perhaps there is a better way, requiring less storage).
        //   -> we use the protobuf way of encoding, we use 7 bits for information and bit 8 indicates if another one follows.
        // - pointer1: the pointer to the next edge for vertex1.
        // - pointer2: the pointer to the next edge for vertex2.
        //   -> we store the diff with the current location (the start of vertex1).
        //   -> we store 0 if there is no next (0 can never be an actual value here).
        private readonly ArrayBase<byte>_edges; // holds the actual edges, in a dual linked-list.
        private readonly int _edgeDataSize; // the size of the data package per edge.
        private readonly int _edgeSize;
        private uint _vertexPointer = 0; // the pointer to the next empty vertex.
        private uint _edgePointer = 0; // the pointer to the next empty edge.
        
        private readonly ShapesArray _shapes = new ShapesArray();

        /// <summary>
        /// Creates a new graph.
        /// </summary>
        /// <param name="zoom">The zoom level.</param>
        /// <param name="edgeDataSize">The size of the data package per edge.</param>
        public Graph(int zoom = 14, int edgeDataSize = 0)
        {
            _zoom = zoom;
            _edgeDataSize = edgeDataSize;
            _edgeSize = 8 * 2 + // the vertices.
                4 * 2 + // the pointers to previous edges.
                _edgeDataSize; // the edge data package.
            
            _tiles = new SparseMemoryArray<byte>(0, emptyDefault: byte.MaxValue);
            _vertices = new MemoryArray<byte>(CoordinateSizeInBytes);
            _edgePointers = new MemoryArray<uint>(1);
            _edges = new MemoryArray<byte>(0);

            for (var p = 0; p < _edgePointers.Length; p++)
            {
                _edgePointers[p] = GraphConstants.TileNotLoaded;
            }
        }

        private Graph(int zoom, int edgeDataSize, int tileSizeInIndex, SparseMemoryArray<byte> tiles,
            int coordinateSizeInBytes, MemoryArray<byte> vertices, uint vertexPointer, MemoryArray<uint> edgePointers,
            uint edgePointer, MemoryArray<byte> edges, ShapesArray shapes)
        {
            _zoom = zoom;
            _edgeDataSize = edgeDataSize;
            _edgeSize = 8 * 2 + // the vertices.
                        4 * 2 + // the pointers to previous edges.
                        _edgeDataSize; // the edge data package.
            
            if (TileSizeInIndex != tileSizeInIndex) throw new ArgumentOutOfRangeException($"{nameof(tileSizeInIndex)}");
            _tiles = tiles;
            if (CoordinateSizeInBytes != coordinateSizeInBytes) throw new ArgumentOutOfRangeException($"{nameof(coordinateSizeInBytes)}");
            _vertices = vertices;
            _vertexPointer = vertexPointer;
            _edgePointers = edgePointers;
            _edgePointer = edgePointer;
            _edges = edges;
            _shapes = shapes;
        }

        /// <summary>
        /// Gets the zoom.
        /// </summary>
        public int Zoom => _zoom;

        /// <summary>
        /// Gets the number of edges in this graph.
        /// </summary>
        public uint EdgeCount => _edgePointer;

        /// <summary>
        /// Gets the number of bytes that can be stored on an edge.
        /// </summary>
        public int EdgeDataSize => _edgeDataSize;

        /// <summary>
        /// Adds a new vertex and returns its ID.
        /// </summary>
        /// <param name="longitude">The longitude.</param>
        /// <param name="latitude">The latitude.</param>
        /// <returns>The ID of the new vertex.</returns>
        public VertexId AddVertex(double longitude, double latitude)
        {
            // get the local tile id.
            var tile = Tile.WorldToTile(longitude, latitude, _zoom);
            var localTileId = tile.LocalId;
            
            // try to find the tile.
            var (vertexPointer, capacity) = FindTile(localTileId);
            if (vertexPointer == GraphConstants.TileNotLoaded)
            {
                (vertexPointer, capacity) = AddTile(localTileId);
            }
            
            // if the tile is at max capacity increase it's capacity.
            long nextEmpty;
            if (_edgePointers.Length <= vertexPointer + capacity - 1 ||
                _edgePointers[vertexPointer + capacity - 1] != GraphConstants.NoVertex)
            { // increase capacity.
                (vertexPointer, capacity) = IncreaseCapacityForTile(localTileId, vertexPointer);
                nextEmpty = (vertexPointer + (capacity / 2));
            }
            else
            { // find the next empty slot.
                nextEmpty = (vertexPointer + capacity - 1);
                if (nextEmpty > vertexPointer)
                { // there may be others that are empty.
                    for (var p = nextEmpty - 1; p >= vertexPointer; p--)
                    {
                        if (_edgePointers[p] != GraphConstants.NoVertex)
                        {
                            break;
                        }
                        nextEmpty = p;
                    }
                }
            }
            var localVertexId = (uint)(nextEmpty - vertexPointer);
            
            // set the vertex data.
            _edgePointers[nextEmpty] = GraphConstants.NoEdges;
            SetEncodedVertex((uint)nextEmpty, tile, longitude, latitude);

            return new VertexId()
            {
                TileId = localTileId,
                LocalId = localVertexId
            };
        }

        /// <summary>
        /// Gets the given vertex.
        /// </summary>
        /// <param name="vertex">The vertex.</param>
        /// <returns>The vertex.</returns>
        public Coordinate GetVertex(VertexId vertex)
        {
            var localTileId = vertex.TileId;
            
            // try to find the tile.
            var (vertexPointer, capacity) = FindTile(localTileId);
            if (vertexPointer == GraphConstants.TileNotLoaded)
                throw new ArgumentException($"{vertex} does not exist.");
            if (vertex.LocalId >= capacity)
                throw new ArgumentException($"{vertex} does not exist.");

            var tile = Tile.FromLocalId(localTileId, _zoom);
            if (!TryGetEncodedVertex(vertexPointer + vertex.LocalId, tile, out var location)) 
                throw new ArgumentException($"{vertex} does not exist.");
            return location;
        }

        /// <summary>
        /// Tries to get the given vertex.
        /// </summary>
        /// <param name="vertex">The vertex.</param>
        /// <param name="location">The location of the vertex.</param>
        /// <returns>The vertex.</returns>
        public bool TryGetVertex(VertexId vertex, out Coordinate location)
        {
            var localTileId = vertex.TileId;
            
            // try to find the tile.
            var (vertexPointer, capacity) = FindTile(localTileId);
            if (vertexPointer == GraphConstants.TileNotLoaded)
            {
                location = default;
                return false;
            }

            if (vertex.LocalId >= capacity)
            {
                location = default;
                return false;
            }

            var tile = Tile.FromLocalId(localTileId, _zoom);
            if (!TryGetEncodedVertex(vertexPointer + vertex.LocalId, tile, out location))
            {
                return false;
            }
            return true;
        }
        
        private void SetEncodedVertex(uint pointer, Tile tile, double longitude, double latitude)
        {
            const int resolution = (1 << TileResolutionInBits) - 1;
            var localCoordinates = tile.ToLocalCoordinates(longitude, latitude, resolution);
            var localCoordinatesEncoded = (localCoordinates.x << TileResolutionInBits) + localCoordinates.y;
            var localCoordinatesBits = BitConverter.GetBytes(localCoordinatesEncoded);
            var vertexPointer = pointer * (long)CoordinateSizeInBytes;
            for (var b = 0; b < CoordinateSizeInBytes; b++)
            {
                _vertices[vertexPointer + b] = localCoordinatesBits[b];
            }
        }

        private bool TryGetEncodedVertex(uint pointer, Tile tile, out Coordinate location)
        {
            const int TileResolutionInBits = (CoordinateSizeInBytes * 8 / 2);
            var vertexPointer = pointer * (long)CoordinateSizeInBytes;

            var bytes = new byte[4];
            var hasData = false;
            for (var b = 0; b < CoordinateSizeInBytes; b++)
            {
                bytes[b] = _vertices[vertexPointer + b];
                if (!hasData && bytes[b] != byte.MaxValue) hasData = true;
            }
            if (!hasData)
            {
                location = default;
                return false;
            }

            var localCoordinatesEncoded = BitConverter.ToInt32(bytes, 0);
            var y = localCoordinatesEncoded % (1 << TileResolutionInBits);
            var x = localCoordinatesEncoded >> TileResolutionInBits;

            const int resolution = (1 << TileResolutionInBits) - 1;
            location = tile.FromLocalCoordinates(x, y, resolution);
            return true;
        }

        private void CopyEncodedVertex(uint pointer1, uint pointer2)
        {
            var vertexPointer1 = pointer1 * CoordinateSizeInBytes;
            var vertexPointer2 = pointer2 * CoordinateSizeInBytes;

            for (var b = 0; b < CoordinateSizeInBytes; b++)
            {
                _vertices[vertexPointer2 + b] = _vertices[vertexPointer1 + b];
            }
        }

        private (uint vertexPointer, int capacity) FindTile(uint localTileId)
        {
            var tilePointerIndex = (long)localTileId * TileSizeInIndex;
            if (tilePointerIndex + TileSizeInIndex >= _tiles.Length)
            {
                return (GraphConstants.TileNotLoaded, 0);
            }

            if (_tiles[tilePointerIndex + 0] == byte.MaxValue &&
                _tiles[tilePointerIndex + 1] == byte.MaxValue &&
                _tiles[tilePointerIndex + 2] == byte.MaxValue &&
                _tiles[tilePointerIndex + 3] == byte.MaxValue &&
                _tiles[tilePointerIndex + 4] == byte.MaxValue)
            {
                return (GraphConstants.TileNotLoaded, 0);
            }
            
            // find an allocation-less way of doing this:
            //   this is possible it .NET core 2.1 but not netstandard2.0,
            //   we can do this in netstandard2.1 normally.
            //   perhaps implement our own version of bitconverter.
            var tileBytes = new byte[4];
            tileBytes[0] = _tiles[tilePointerIndex + 0];
            tileBytes[1] = _tiles[tilePointerIndex + 1];
            tileBytes[2] = _tiles[tilePointerIndex + 2];
            tileBytes[3] = _tiles[tilePointerIndex + 3];

            return (BitConverter.ToUInt32(tileBytes, 0), 1 << _tiles[tilePointerIndex + 4]);
        }

        private (uint vertexPointer, int capacity) AddTile(uint localTileId)
        {
            var tilePointerIndex = (long)localTileId * TileSizeInIndex;
            if (tilePointerIndex + TileSizeInIndex >= _tiles.Length)
            {
                _tiles.Resize(tilePointerIndex + TileSizeInIndex + 1024);
            }
            
            var tileBytes = BitConverter.GetBytes(_vertexPointer);
            _tiles[tilePointerIndex + 0] = tileBytes[0];
            _tiles[tilePointerIndex + 1] = tileBytes[1];
            _tiles[tilePointerIndex + 2] = tileBytes[2];
            _tiles[tilePointerIndex + 3] = tileBytes[3];
            _tiles[tilePointerIndex + 4] = DefaultTileCapacityInBits;
            
            const int capacity = 1 << DefaultTileCapacityInBits;
            var pointer = _vertexPointer;
            _vertexPointer += capacity;
            return (pointer, capacity);
        }

        private (uint vertexPointer, int capacity) IncreaseCapacityForTile(uint localTileId, uint pointer)
        {
            // copy current data, we assume current capacity is at max.
            var tilePointer = (long)localTileId * TileSizeInIndex;
            
            // get current capacity and double it.
            var capacityInBits = _tiles[tilePointer + 4];
            _tiles[tilePointer + 4] = (byte)(capacityInBits + 1);
            var capacity = 1 << capacityInBits;

            // get the current pointer and update it.
            var newVertexPointer = _vertexPointer;
            _vertexPointer += (uint)(capacity * 2);
            var pointerBytes = BitConverter.GetBytes(newVertexPointer); 
            for (var b = 0; b < 4; b++)
            {
                _tiles[tilePointer + b] = pointerBytes[b];
            }
            
            // make sure edge pointers array and vertex coordinates arrays are the proper sizes.
            var length = _edgePointers.Length;
            while (_vertexPointer + capacity >= length)
            {
                length += 1024;
            }

            if (length != _edgePointers.Length)
            {
                var sizeBefore = _edgePointers.Length;
                _edgePointers.Resize(length);
                for (var p = sizeBefore; p < _edgePointers.Length; p++)
                {
                    _edgePointers[p] = GraphConstants.NoVertex;
                }

                sizeBefore = _vertices.Length;
                _vertices.Resize(length * CoordinateSizeInBytes);
                for (var p = sizeBefore; p < _vertices.Length; p++)
                {
                    _vertices[p] = byte.MaxValue;
                }
            }
            
            // copy all the data over.
            for (uint p = 0; p < capacity; p++)
            {
                _edgePointers[newVertexPointer + p] = _edgePointers[pointer + p];
                CopyEncodedVertex(pointer + p, newVertexPointer + p);
            }

            return (newVertexPointer, capacity * 2);
        }

        private int WriteToEdges(long pointer, VertexId vertex)
        {
            WriteToEdges(pointer, vertex.TileId);
            WriteToEdges(pointer + 4, vertex.LocalId);

            return 8;
        }

        private int WriteToEdges(long pointer, uint data)
        {
            // TODO: build an allocation-less version.
            var bytes = BitConverter.GetBytes(data);

            _edges[pointer + 0] = bytes[0];
            _edges[pointer + 1] = bytes[1];
            _edges[pointer + 2] = bytes[2];
            _edges[pointer + 3] = bytes[3];

            return 4;
        }

        private int WriteToEdges(long pointer, IReadOnlyList<byte> data)
        {
            if (data == null)
            {
                for (var i = 0; i < _edgeDataSize; i++)
                {
                    _edges[pointer + i] = byte.MaxValue;;
                }

                return _edgeDataSize;
            }

            for (var i = 0; i < _edgeDataSize; i++)
            {
                if (i < data.Count)
                {
                    _edges[pointer + i] = data[i];
                }
                else
                {
                    _edges[pointer + i] = byte.MaxValue;
                }
            }

            return _edgeDataSize;
        }

        private VertexId ReadFromEdgeVertexId(long pointer)
        {
            return new VertexId()
            {
                TileId = ReadFromEdgeUInt32(pointer),
                LocalId = ReadFromEdgeUInt32(pointer + 4)
            };
        }

        private uint ReadFromEdgeUInt32(long pointer)
        {
            var bytes = new byte[4];
            bytes[0] = _edges[pointer + 0];
            bytes[1] = _edges[pointer + 1];
            bytes[2] = _edges[pointer + 2];
            bytes[3] = _edges[pointer + 3];

            return BitConverter.ToUInt32(bytes, 0);
        }

        /// <summary>
        /// Adds a new edge and it's inline data.
        /// </summary>
        /// <param name="vertex1">The first vertex.</param>
        /// <param name="vertex2">The second vertex.</param>
        /// <param name="data">The inline data.</param>
        /// <param name="shape">The edge shape.</param>
        /// <returns>The edge id.</returns>
        public uint AddEdge(VertexId vertex1, VertexId vertex2, IReadOnlyList<byte> data = null,
            IEnumerable<Coordinate> shape = null)
        {
            // try to find vertex1.
            var (vertex1Pointer, capacity1) = FindTile(vertex1.TileId);
            if (vertex1Pointer == GraphConstants.TileNotLoaded ||
                vertex1.LocalId >= capacity1)
            {
                throw new ArgumentException($"{vertex1} does not exist.");
            }
            
            // try to find vertex2.
            var (vertex2Pointer, capacity2) = FindTile(vertex2.TileId);
            if (vertex2Pointer == GraphConstants.TileNotLoaded ||
                vertex2.LocalId >= capacity2)
            {
                throw new ArgumentException($"{vertex2} does not exist.");
            }
            
            // get edge pointers.
            var edgePointer1 = _edgePointers[vertex1Pointer + vertex1.LocalId];
            var edgePointer2 = _edgePointers[vertex2Pointer + vertex2.LocalId];
            
            // make sure there is enough space.
            var rawPointer = (_edgePointer * _edgeSize);
            if (rawPointer + _edgeSize > _edges.Length)
            {
                _edges.EnsureMinimumSize(rawPointer + _edgeSize);
            }
            
            // add edge pointers with new edge.
            rawPointer += WriteToEdges(rawPointer, vertex1);
            rawPointer += WriteToEdges(rawPointer, vertex2);
            // write pointer to previous edge.
            if (edgePointer1 == GraphConstants.NoEdges)
            { // if there is no previous edge, write 0
                rawPointer += WriteToEdges(rawPointer, 0); 
            }
            else
            { // write pointer but offset by 1.
                rawPointer += WriteToEdges(rawPointer, edgePointer1 + 1);
            }
            // write pointer to previous edge.
            if (edgePointer2 == GraphConstants.NoEdges)
            { // if there is no previous edge, write 0
                rawPointer += WriteToEdges(rawPointer, 0); 
            }
            else
            { // write pointer but offset by 1.
                rawPointer += WriteToEdges(rawPointer, edgePointer2 + 1);
            }
            rawPointer += WriteToEdges(rawPointer, data); // write data package.
            
            // update edge pointers.
            var newEdgePointer = _edgePointer;
            _edgePointers[vertex1Pointer + vertex1.LocalId] = newEdgePointer;
            _edgePointers[vertex2Pointer + vertex2.LocalId] = newEdgePointer;
            _edgePointer += 1;

            // add shape if any.
            if (shape != null)
            {
                if (_shapes.Length <= newEdgePointer)
                { // TODO: this resizing should be in the shapes array.
                    _shapes.Resize(newEdgePointer + 1024);
                }
                _shapes[newEdgePointer] = new ShapeEnumerable(shape);
            }

            return newEdgePointer;
        }

        /// <summary>
        /// Gets an edge enumerator.
        /// </summary>
        /// <returns></returns>
        internal Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        internal class Enumerator
        {
            private VertexId _vertex;
            private bool _firstEdge;
            private uint _rawPointer;
            private uint _nextRawPointer;
            private bool _forward;
            private readonly Graph _graph;

            internal Enumerator(Graph graph)
            {
                _forward = false;
                _firstEdge = true;
                _vertex = VertexId.Empty;
                _rawPointer = uint.MaxValue;
                _nextRawPointer = uint.MaxValue;
                _graph = graph;

                this.To = VertexId.Empty;
            }
            
            /// <summary>
            /// Moves the enumerator to the first edge of the given vertex.
            /// </summary>
            /// <param name="vertex">The vertex.</param>
            /// <returns>True if the vertex exists.</returns>
            public bool MoveTo(VertexId vertex)
            {
                _firstEdge = true;
                _forward = false;
                _vertex = vertex;
                _rawPointer = uint.MaxValue;
                _nextRawPointer = uint.MaxValue;
                
                // try to find vertex.
                var (vertex1Pointer, capacity1) =  _graph.FindTile(vertex.TileId);
                if (vertex1Pointer == GraphConstants.TileNotLoaded ||
                    vertex.LocalId >= capacity1)
                {
                    return false;
                }
                
                // get edge pointer.
                var edgePointer = _graph._edgePointers[vertex1Pointer + vertex.LocalId];
                
                // set the raw pointer if there is data.
                if (edgePointer == GraphConstants.NoEdges)
                {
                    _rawPointer = GraphConstants.NoEdges;
                }
                else if (edgePointer == uint.MaxValue)
                {               
                    // try to find vertex again, tile was moved?.
                    (vertex1Pointer, capacity1) =  _graph.FindTile(vertex.TileId);
                    if (vertex1Pointer == GraphConstants.TileNotLoaded ||
                        vertex.LocalId >= capacity1)
                    {
                        return false;
                    }
                
                    // get edge pointer.
                    edgePointer = _graph._edgePointers[vertex1Pointer + vertex.LocalId];
                }
                else
                {
                    _rawPointer = (uint)(edgePointer * _graph._edgeSize);
                }

                return true;
            }

            /// <summary>
            /// Moves the enumerator to the given edge. 
            /// </summary>
            /// <param name="edgeId">The edge id.</param>
            /// <param name="forward">The forward flag, when false the enumerator is in a state as it was enumerated to the edge via its last vertex. When true the enumerator is in a state as it was enumerated to the edge via its first vertex.</param>
            public bool MoveToEdge(uint edgeId, bool forward = true)
            {
                _forward = false;
                _vertex = VertexId.Empty;
                _rawPointer = uint.MaxValue;
                _nextRawPointer = uint.MaxValue;

                // build raw edge pointer.
                _rawPointer = (uint)(edgeId * _graph._edgeSize);
                if (_graph._edges.Length <= _rawPointer)
                {
                    _rawPointer = uint.MaxValue;
                    return false;
                }

                // set the state of the enumerator.
                if (forward)
                {
                    _firstEdge = false;
                    _vertex = _graph.ReadFromEdgeVertexId(_rawPointer);
                    var vertex2 = _graph.ReadFromEdgeVertexId(_rawPointer + 8);
                    _forward = true;
                    this.To = vertex2;
                    _nextRawPointer = (uint) ((_graph.ReadFromEdgeUInt32(_rawPointer + 16) - 1) * _graph._edgeSize);
                }
                else
                {
                    _firstEdge = false;
                    _vertex = _graph.ReadFromEdgeVertexId(_rawPointer + 8);
                    var vertex2 = _graph.ReadFromEdgeVertexId(_rawPointer);
                    _forward = false;
                    this.To = vertex2;
                    _nextRawPointer = (uint) ((_graph.ReadFromEdgeUInt32(_rawPointer + 16 + 4) - 1) * _graph._edgeSize);
                }

                return true;
            }

            /// <summary>
            /// Resets this enumerator to the first edge of the last vertex that was moved to if any.
            /// </summary>
            public void Reset()
            {
                if (_vertex.IsEmpty()) return;

                this.MoveTo(_vertex);
            }

            /// <summary>
            /// Moves this enumerator to the next edge.
            /// </summary>
            /// <returns>True if there is data available.</returns>
            public bool MoveNext()
            {
                if (_firstEdge)
                { // move to first edge.
                    _firstEdge = false;
                    if (_vertex.IsEmpty())
                    {
                        return false;
                    }

                    if (_rawPointer == GraphConstants.NoEdges)
                    {
                        return false;
                    }
                }
                else
                {
                    if (_nextRawPointer == GraphConstants.NoEdges)
                    {
                        return false;
                    }

                    _rawPointer = _nextRawPointer;
                }
                    
                // get details, determine direction, and get the pointer to next.
                var vertex1 = _graph.ReadFromEdgeVertexId(_rawPointer);
                var vertex2 = _graph.ReadFromEdgeVertexId(_rawPointer + 8);
                uint nextEdgePointer;
                if (vertex1 == _vertex)
                {
                    _forward = true;
                    this.To = vertex2;
                    nextEdgePointer = _graph.ReadFromEdgeUInt32(_rawPointer + 16);
                }
                else
                {
                    _forward = false;
                    this.To = vertex1;
                    nextEdgePointer = _graph.ReadFromEdgeUInt32(_rawPointer + 16 + 4);
                }
                _nextRawPointer = GraphConstants.NoEdges;
                if (nextEdgePointer != 0) _nextRawPointer = (uint)((nextEdgePointer - 1) * _graph._edgeSize);
                
                return true;
            }

            /// <summary>
            /// Returns true if the edge is from -> to, false otherwise.
            /// </summary>
            public bool Forward => _forward;

            /// <summary>
            /// Gets the source vertex.
            /// </summary>
            public VertexId From => _vertex;
            
            /// <summary>
            /// Gets the target vertex.
            /// </summary>
            public VertexId To { get; private set; }

            /// <summary>
            /// Gets the edge id.
            /// </summary>
            public uint Id => (uint)(_rawPointer / _graph._edgeSize);
            
            /// <summary>
            /// Gets the shape, if any.
            /// </summary>
            /// <returns>The shape.</returns>
            public ShapeBase GetShape()
            {
                var shape = _graph._shapes[this.Id];
                if (shape == null) return null;
                if (!this.Forward) shape = shape.Reverse();
                return shape;
            }
            
            /// <summary>
            /// Copies the data to the given array.
            /// </summary>
            /// <param name="data">The target array.</param>
            /// <param name="start">The position to start copying in the given array.</param>
            /// <returns>The # of bytes copied.</returns>
            public int CopyDataTo(byte[] data, int start = 0)
            {
                if (_firstEdge || _rawPointer == GraphConstants.NoEdges) return 0;

                var count = _graph._edgeDataSize;
                if (data.Length - start < count) count = data.Length - start;
                for (var i = 0; i < count; i++)
                {
                    data[start + i] =  _graph._edges[_rawPointer + 24 + i];
                }

                return count;
            }

            /// <summary>
            /// Gets the data on the current edge.
            /// </summary>
            public byte[] Data
            {
                get
                {
                    if (_firstEdge || _rawPointer == GraphConstants.NoEdges) return new byte[0];
                    
                    var data = new byte[_graph._edgeDataSize];
                    this.CopyDataTo(data);
                    return data;
                }
            }

            // TODO: these below are only exposed for the edge data coders, figure out if we can do this in a better way. This externalizes some of the graphs internal structure.
            
            /// <summary>
            /// Gets the internal raw pointer for the edge.
            /// </summary>
            internal uint RawPointer => _rawPointer;

            /// <summary>
            /// Gets the raw edges array.
            /// </summary>
            internal ArrayBase<byte> Edges => _graph._edges;

            /// <summary>
            /// Gets the graph this enumerator is for.
            /// </summary>
            internal Graph Graph => _graph;

            /// <summary>
            /// Gets the edge size.
            /// </summary>
            internal int EdgeSize => _graph._edgeSize;
        }

        internal long WriteTo(Stream stream)
        {
            var p = stream.Position;
            
            // write header and version.
            stream.WriteWithSize($"{nameof(Graph)}");
            stream.WriteByte(1);
            
            // writes zoom and edge data size.
            stream.WriteByte((byte)_zoom);
            stream.WriteByte((byte)_edgeDataSize);
            
            // write tile index.
            stream.WriteByte((byte)TileSizeInIndex);
            _tiles.CopyToWithHeader(stream);
            
            // write vertices.
            stream.WriteByte((byte)CoordinateSizeInBytes);
            stream.Write(BitConverter.GetBytes((long) _vertexPointer), 0, 8);
            _vertices.CopyToWithSize(stream);
            _edgePointers.CopyToWithSize(stream);
            
            // write edges.
            stream.Write(BitConverter.GetBytes((long) _edgePointer), 0, 8);
            _edges.CopyToWithSize(stream);
            
            // write shapes.
            _shapes.CopyTo(stream);

            return stream.Position - p;
        }

        internal static Graph ReadFrom(Stream stream)
        {
            // read & verify header.
            var header = stream.ReadWithSizeString();
            var version = stream.ReadByte();
            if (header != nameof(Graph)) throw new InvalidDataException($"Cannot read {nameof(Graph)}: Header invalid.");
            if (version != 1) throw new InvalidDataException($"Cannot read {nameof(Graph)}: Version # invalid.");
            
            // read zoom and edge data size.
            var zoom = stream.ReadByte();
            var edgeDataSize = stream.ReadByte();
            
            // read tile index.
            var tileSizeIndex = stream.ReadByte();
            var tiles = SparseMemoryArray<byte>.CopyFromWithHeader(stream);
            
            // read vertices.
            var coordinateSizeInBytes = stream.ReadByte();
            var vertexPointer = stream.ReadInt64();
            var vertices = MemoryArray<byte>.CopyFromWithSize(stream);
            var edgePointers = MemoryArray<uint>.CopyFromWithSize(stream);
            
            // read edges.
            var edgePointer = stream.ReadInt64();
            var edges = MemoryArray<byte>.CopyFromWithSize(stream);
            
            // read shapes.
            var shapes = ShapesArray.CreateFrom(stream, true, false);
            
            return new Graph(zoom, edgeDataSize, tileSizeIndex, tiles, coordinateSizeInBytes, vertices, (uint)vertexPointer, edgePointers,
                (uint)edgePointer, edges, shapes);
        }
    }
}