﻿using System;

namespace Itinero.Data.Tiles
{
    public class Tile
    {
        public Tile(uint x, uint y, uint zoom)
        {
            this.X = x;
            this.Y = y;
            this.Zoom = zoom;

            this.CalculateBounds();
        }
        
        private void CalculateBounds()
        {
            var n = Math.PI - ((2.0 * Math.PI * this.Y) / Math.Pow(2.0, this.Zoom));
            this.Left = ((this.X / Math.Pow(2.0, this.Zoom) * 360.0) - 180.0);
            this.Top = (180.0 / Math.PI * Math.Atan(Math.Sinh(n)));

            n = Math.PI - ((2.0 * Math.PI * (this.Y + 1)) / Math.Pow(2.0, this.Zoom));
            this.Right = ((this.X + 1) / Math.Pow(2.0, this.Zoom) * 360.0) - 180.0;
            this.Bottom = (180.0 / Math.PI * Math.Atan(Math.Sinh(n)));
        }
        
        /// <summary>
        /// Gets X.
        /// </summary>
        public uint X { get; private set; }

        /// <summary>
        /// Gets Y.
        /// </summary>
        public uint Y { get; private set; }

        /// <summary>
        /// Gets the zoom level.
        /// </summary>
        public uint Zoom { get; private set; }
        
        /// <summary>
        /// Gets the top.
        /// </summary>
        public double Top { get; private set; }

        /// <summary>
        /// Get the bottom.
        /// </summary>
        public double Bottom { get; private set; }

        /// <summary>
        /// Get the left.
        /// </summary>
        public double Left { get; private set; }

        /// <summary>
        /// Gets the right.
        /// </summary>
        public double Right { get; private set; }

        /// <summary>
        /// Updates the data in this tile to correspond with the given local tile id.
        /// </summary>
        /// <param name="localId">The local tile id.</param>
        public void UpdateToLocalId(uint localId)
        {
            var xMax = (ulong) (1 << (int) this.Zoom);

            this.X = (uint) (localId % xMax);
            this.Y = (uint) (localId / xMax);
        }

        /// <summary>
        /// Converts a lat/lon pair to a set of local coordinates.
        /// </summary>
        /// <param name="latitude">The latitude.</param>
        /// <param name="longitude">The longitude.</param>
        /// <param name="resolution">The resolution.</param>
        /// <returns>A local coordinate pair.</returns>
        public (int x, int y) ToLocalCoordinates(double latitude, double longitude, int resolution)
        {
            var latStep = (this.Top - this.Bottom) / resolution;
            var lonStep = (this.Right - this.Left) / resolution;
            var top = this.Top;
            var left = this.Left;
            
            return ((int) ((longitude - left) / lonStep), (int) ((top - latitude) / latStep));
        }
        
        /// <summary> 
        /// Converts a set of local coordinates to a lat/lon pair.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="resolution"></param>
        /// <returns>A global coordinate pair.</returns>
        public (double latitude, double longitude) FromLocalCoordinates(int x, int y, int resolution)
        {
            var latStep = (this.Top - this.Bottom) / resolution;
            var lonStep = (this.Right - this.Left) / resolution;
            var top = this.Top;
            var left = this.Left;

            return (left + (lonStep * x), top - (y * latStep));
        }
        
        /// <summary>
        /// Gets the tile at the given coordinates for the given zoom level.
        /// </summary>
        /// <param name="latitude">The latitude.</param>
        /// <param name="longitude">The longitude.</param>
        /// <param name="zoom">The zoom level.</param>
        /// <returns>The tile a the given coordinates.</returns>
        public static Tile WorldToTile(double latitude, double longitude, uint zoom)
        {
            var n = (int) Math.Floor(Math.Pow(2, zoom));

            var rad = (latitude / 180d) * System.Math.PI;

            var x = (uint) ((longitude + 180.0f) / 360.0f * n);
            var y = (uint) (
                (1.0f - Math.Log(Math.Tan(rad) + 1.0f / Math.Cos(rad))
                 / Math.PI) / 2f * n);

            return new Tile (x, y, zoom);
        }
    }
}