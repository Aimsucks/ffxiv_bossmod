﻿using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BossMod
{
    public abstract class AOEShape
    {
        public abstract bool Check(WPos position, WPos origin, Angle rotation);
        public abstract void Draw(MiniArena arena, WPos origin, Angle rotation);
        public abstract void Outline(MiniArena arena, WPos origin, Angle rotation);

        public bool Check(WPos position, Actor? origin)
        {
            return origin != null ? Check(position, origin.Position, origin.Rotation) : false;
        }

        public void Draw(MiniArena arena, Actor? origin)
        {
            if (origin != null)
                Draw(arena, origin.Position, origin.Rotation);
        }

        public void Outline(MiniArena arena, Actor? origin)
        {
            if (origin != null)
                Outline(arena, origin.Position, origin.Rotation);
        }
    }

    public class AOEShapeCone : AOEShape
    {
        public float Radius;
        public Angle DirectionOffset;
        public Angle HalfAngle;

        public AOEShapeCone(float radius, Angle halfAngle, Angle directionOffset = new())
        {
            Radius = radius;
            DirectionOffset = directionOffset;
            HalfAngle = halfAngle;
        }

        public override bool Check(WPos position, WPos origin, Angle rotation) => position.InCircleCone(origin, Radius, rotation + DirectionOffset, HalfAngle);
        public override void Draw(MiniArena arena, WPos origin, Angle rotation) => arena.ZoneCone(origin, 0, Radius, rotation + DirectionOffset, HalfAngle, arena.ColorAOE);
        public override void Outline(MiniArena arena, WPos origin, Angle rotation) => arena.AddCone(origin, Radius, rotation + DirectionOffset, HalfAngle, arena.ColorDanger);
    }

    public class AOEShapeCircle : AOEShape
    {
        public float Radius;

        public AOEShapeCircle(float radius)
        {
            Radius = radius;
        }

        public override bool Check(WPos position, WPos origin, Angle rotation) => position.InCircle(origin, Radius);
        public override void Draw(MiniArena arena, WPos origin, Angle rotation) => arena.ZoneCircle(origin, Radius, arena.ColorAOE);
        public override void Outline(MiniArena arena, WPos origin, Angle rotation) => arena.AddCircle(origin, Radius, arena.ColorDanger);
    }

    public class AOEShapeDonut : AOEShape
    {
        public float InnerRadius;
        public float OuterRadius;

        public AOEShapeDonut(float innerRadius, float outerRadius)
        {
            InnerRadius = innerRadius;
            OuterRadius = outerRadius;
        }

        public override bool Check(WPos position, WPos origin, Angle rotation) => position.InDonut(origin, InnerRadius, OuterRadius);
        public override void Draw(MiniArena arena, WPos origin, Angle rotation) => arena.ZoneDonut(origin, InnerRadius, OuterRadius, arena.ColorAOE);
        public override void Outline(MiniArena arena, WPos origin, Angle rotation)
        {
            arena.AddCircle(origin, InnerRadius, arena.ColorDanger);
            arena.AddCircle(origin, OuterRadius, arena.ColorDanger);
        }
    }

    public class AOEShapeRect : AOEShape
    {
        public float LengthFront;
        public float LengthBack;
        public float HalfWidth;
        public Angle DirectionOffset;

        public AOEShapeRect(float lengthFront, float halfWidth, float lengthBack = 0, Angle directionOffset = new())
        {
            LengthFront = lengthFront;
            LengthBack = lengthBack;
            HalfWidth = halfWidth;
            DirectionOffset = directionOffset;
        }

        public override bool Check(WPos position, WPos origin, Angle rotation) => position.InRect(origin, rotation + DirectionOffset, LengthFront, LengthBack, HalfWidth);
        public override void Draw(MiniArena arena, WPos origin, Angle rotation) => arena.ZoneRect(origin, rotation + DirectionOffset, LengthFront, LengthBack, HalfWidth, arena.ColorAOE);
        public override void Outline(MiniArena arena, WPos origin, Angle rotation)
        {
            var direction = (rotation + DirectionOffset).ToDirection();
            var side = HalfWidth * direction.OrthoR();
            var front = origin + LengthFront * direction;
            var back = origin - LengthBack * direction;
            arena.AddQuad(front + side, front - side, back - side, back + side, arena.ColorDanger);
        }

        public void SetEndPoint(WPos endpoint, WPos origin, Angle rotation)
        {
            // this is a bit of a hack, but whatever...
            var dir = endpoint - origin;
            LengthFront = dir.Length();
            DirectionOffset = Angle.FromDirection(dir) - rotation;
        }

        public void SetEndPointFromCastLocation(Actor caster)
        {
            if (caster.CastInfo != null)
                SetEndPoint(caster.CastInfo.LocXZ, caster.Position, caster.Rotation);
        }
    }

    public class AOEShapeMulti : AOEShape
    {
        public List<AOEShape> Shapes;

        public AOEShapeMulti(IEnumerable<AOEShape> shapes)
        {
            Shapes = new(shapes);
        }

        public override bool Check(WPos position, WPos origin, Angle rotation)
        {
            return Shapes.Any(s => s.Check(position, origin, rotation));
        }

        public override void Draw(MiniArena arena, WPos origin, Angle rotation)
        {
            foreach (var s in Shapes)
                s.Draw(arena, origin, rotation);
        }

        public override void Outline(MiniArena arena, WPos origin, Angle rotation)
        {
            foreach (var s in Shapes)
                s.Outline(arena, origin, rotation);
        }
    }
}
