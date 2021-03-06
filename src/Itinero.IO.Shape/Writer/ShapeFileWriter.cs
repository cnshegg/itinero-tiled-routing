/*
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

using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Itinero.IO.Shape.Writer
{
    /// <summary>
    /// A writer that writes shapefile(s) and builds a routing network.
    /// </summary>
    internal class ShapeFileWriter
    {
        private readonly RouterDb _routerDb;
        private readonly string _fileName;

        /// <summary>
        /// Creates a new reader.
        /// </summary>
        public ShapeFileWriter(RouterDb routerDb, string fileName)
        {
            _routerDb = routerDb;
            _fileName = fileName;
        }

        /// <summary>
        /// Executes the actual algorithm.
        /// </summary>
        public void Run(CancellationToken cancellationToken)
        {            
            // assumed here all arguments are as they should be.
            var features = new FeaturesList(_routerDb);
            if (features.Count == 0)
            {
                return;
            }

            var header = ShapefileDataWriter.GetHeader(features[0], features.Count);
            var shapeWriter = new ShapefileDataWriter(_fileName, new GeometryFactory())
            {
                Header = header
            };
            shapeWriter.Write(features);
        }
    }
}