﻿using Microsoft.Xna.Framework;
using SadConsole.Entities;
using System.Collections.Generic;

namespace Bomberman.Client.GameObjects
{
    public class Bomb : Entity
    {
        public int Id;
        protected readonly int _strength;
        private readonly Grid _grid;

        protected readonly Player _placedBy;

        public Bomb(Player placedBy, Point position, int strength, int id) : base(Color.White, Color.Transparent, 3)
        {
            Id = id;
            Position = position;
            Font = Game.Font;
            _placedBy = placedBy;
            _strength = strength;
            _grid = Game.GridScreen.Grid;
            _grid.GetValue(position.X, position.Y).HasBomb = true;
        }

        public void StartDetonationPhase()
        {
            // TODO
        }

        private List<Point> _cellPositions;
        public List<Point> GetCellPositions()
        {
            if (_cellPositions != null) return _cellPositions;
            var cells = new List<Point>
            {
                Position
            };

            // Check each direction and expand 1 cell for each strength level
            bool checkRight = true;
            bool checkLeft = true;
            bool checkUp = true;
            bool checkDown = true;
            for (int i = 1; i <= _strength; i++)
            {
                if (checkRight)
                {
                    var right = _grid.GetValue(Position.X + i, Position.Y);
                    checkRight = right != null && right.Explored && right.Destroyable;
                    if (right != null && right.Destroyable)
                        cells.Add(right.Position);
                }
                if (checkLeft)
                {
                    var left = _grid.GetValue(Position.X - i, Position.Y);
                    checkLeft = left != null && left.Explored && left.Destroyable;
                    if (left != null && left.Destroyable)
                        cells.Add(left.Position);
                }
                if (checkUp)
                {
                    var up = _grid.GetValue(Position.X, Position.Y - i);
                    checkUp = up != null && up.Explored && up.Destroyable;
                    if (up != null && up.Destroyable)
                        cells.Add(up.Position);
                }
                if (checkDown)
                {
                    var down = _grid.GetValue(Position.X, Position.Y + i);
                    checkDown = down != null && down.Explored && down.Destroyable;
                    if (down != null && down.Destroyable)
                        cells.Add(down.Position);
                }
            }

            return _cellPositions = cells;
        }

        public void CleanupFireAfter()
        {
            var cellPositions = GetCellPositions();
            foreach (var pos in cellPositions)
            {
                var cell = _grid.GetValue(pos.X, pos.Y);

                if (cell.ContainsFireFrom.Count > 1)
                {
                    cell.ContainsFireFrom.Remove(Id);
                    continue; // Let other bomb handle this one
                }

                _grid.Explore(cell.Position.X, cell.Position.Y);
                cell.ContainsFireFrom.Remove(Id);
            }

            Game.GridScreen.IsDirty = true;
            Parent = null;
        }

        public void Detonate()
        {
            Animation[0].Foreground = Color.Transparent;
            Animation.IsDirty = true;
            Parent = null;

            // Remove from bombs collection
            _grid.Bombs.Remove(Position);

            // Remove from bombs collection
            foreach (var pos in GetCellPositions())
            {
                var cell = _grid.GetValue(pos.X, pos.Y);

                // Delete existing power ups in this location
                _grid.DeletePowerUp(cell.Position);

                // Set cell on fire
                cell.ContainsFireFrom.Add(Id);
                cell.Glyph = 4;
                cell.Foreground = Color.White;
            }

            Game.GridScreen.IsDirty = true;
        }
    }
}
