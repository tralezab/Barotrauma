﻿using Barotrauma.Networking;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class Sonar : Powered, IServerSerializable, IClientSerializable
    {
        private GUITickBox isActiveTickBox;

        private float displayBorderSize;

        private List<SonarBlip> sonarBlips;

        private float prevPingRadius;

        private float prevPassivePingRadius;

        private Vector2 center;
        private float displayRadius;
        private float displayScale;

        //Vector2 = vector from the ping source to the position of the disruption
        //float = strength of the disruption, between 0-1
        List<Pair<Vector2, float>> disruptedDirections = new List<Pair<Vector2, float>>();

        private static Color[] blipColorGradient =
        {
            Color.TransparentBlack,
            new Color(0, 50, 160),
            new Color(0, 133, 166),
            new Color(2, 159, 30),
            new Color(255, 255, 255)
        };

        public float DisplayRadius
        {
            get { return displayRadius; }
        }

        partial void InitProjSpecific(XElement element)
        {
            displayBorderSize = element.GetAttributeFloat("displaybordersize", 0.0f);

            sonarBlips = new List<SonarBlip>();
            var paddedFrame = new GUIFrame(new RectTransform(new Vector2(0.95f, 0.9f), GuiFrame.RectTransform, Anchor.Center), style: null)
            {
                CanBeFocused = false
            };

            isActiveTickBox = new GUITickBox(new RectTransform(new Point(20, 20), paddedFrame.RectTransform), "Active Sonar")
            {
                OnSelected = (GUITickBox box) =>
                {
                    if (GameMain.Server != null)
                    {
                        item.CreateServerEvent(this);
                    }
                    else if (GameMain.Client != null)
                    {
                        item.CreateClientEvent(this);
                        correctionTimer = CorrectionDelay;
                    }
                    IsActive = box.Selected;

                    return true;
                }
            };

            new GUICustomComponent(new RectTransform(new Point(GuiFrame.Rect.Height, GuiFrame.Rect.Width), GuiFrame.RectTransform, Anchor.CenterRight) { AbsoluteOffset = new Point(10, 0) },
                (spriteBatch, guiCustomComponent) => { DrawSonar(spriteBatch, guiCustomComponent.Rect); }, null);

            GuiFrame.CanBeFocused = false;
        }

        public override void AddToGUIUpdateList()
        {
            GuiFrame.AddToGUIUpdateList();
        }

        public override void UpdateHUD(Character character, float deltaTime)
        {
            for (int i = sonarBlips.Count - 1; i >= 0; i--)
            {
                sonarBlips[i].FadeTimer -= deltaTime * 0.5f;
                sonarBlips[i].Position += sonarBlips[i].Velocity * deltaTime;

                if (sonarBlips[i].FadeTimer <= 0.0f) sonarBlips.RemoveAt(i);
            }
            
            if (UseTransducers && connectedTransducers.Count == 0)
            {
                return;
            }

            Vector2 transducerCenter = UseTransducers ? GetTransducerCenter() : item.WorldPosition;

            if (Level.Loaded != null)
            {
                Dictionary<LevelTrigger, Vector2> levelTriggerFlows = new Dictionary<LevelTrigger, Vector2>();
                foreach (LevelObject levelObject in Level.Loaded.LevelObjectManager.GetAllObjects(transducerCenter, range * pingState))
                {
                    //gather all nearby triggers that are causing the water to flow into the dictionary
                    foreach (LevelTrigger trigger in levelObject.Triggers)
                    {
                        Vector2 flow = trigger.GetWaterFlowVelocity();
                        //ignore ones that are barely doing anything (flow^2 < 1)
                        if (flow.LengthSquared() > 1.0f) levelTriggerFlows.Add(trigger, flow);
                    }
                }

                foreach (KeyValuePair<LevelTrigger, Vector2> triggerFlow in levelTriggerFlows)
                {
                    LevelTrigger trigger = triggerFlow.Key;
                    Vector2 flow = triggerFlow.Value;

                    float flowMagnitude = flow.Length();
                    if (Rand.Range(0.0f, 1.0f) < flowMagnitude / 1000.0f)
                    {
                        float edgeDist = Rand.Range(0.0f, 1.0f);
                        Vector2 blipPos = trigger.WorldPosition + Rand.Vector(trigger.ColliderRadius * edgeDist);
                        Vector2 blipVel = flow;
                        if (trigger.ForceFalloff) flow *= (1.0f - edgeDist);

                        //go through other triggers in range and add the flows of the ones that the blip is inside
                        foreach (KeyValuePair<LevelTrigger, Vector2> triggerFlow2 in levelTriggerFlows)
                        {
                            LevelTrigger trigger2 = triggerFlow2.Key;
                            if (trigger2 != trigger && Vector2.DistanceSquared(blipPos, trigger2.WorldPosition) < trigger2.ColliderRadius * trigger2.ColliderRadius)
                            {
                                Vector2 trigger2flow = triggerFlow2.Value;
                                if (trigger2.ForceFalloff) trigger2flow *= (1.0f - Vector2.Distance(blipPos, trigger2.WorldPosition) / trigger2.ColliderRadius);
                                blipVel += trigger2flow;
                            }
                        }
                        var flowBlip = new SonarBlip(blipPos, Rand.Range(0.5f, 1.0f), 1.0f)
                        {
                            Velocity = blipVel * Rand.Range(1.0f, 5.0f),
                            Size = new Vector2(MathHelper.Lerp(0.4f, 5f, flowMagnitude / 500.0f), 0.2f),
                            Rotation = (float)Math.Atan2(-blipVel.Y, blipVel.X)
                        };
                        sonarBlips.Add(flowBlip);
                    }
                }
            }            
            
            if (IsActive)
            {
                float pingRadius = displayRadius * pingState;
                UpdateDisruptions(transducerCenter, pingRadius / displayScale, prevPingRadius / displayScale);
                Ping(transducerCenter, transducerCenter, 
                    pingRadius, prevPingRadius, displayScale, range, 2.0f);
                prevPingRadius = pingRadius;
                return;
            }

            float passivePingRadius = (float)Math.Sin(Timing.TotalTime * 10);
            if (passivePingRadius > 0.0f)
            {
                disruptedDirections.Clear();
                foreach (AITarget t in AITarget.List)
                {
                    if (t.SoundRange <= 0.0f || !t.Enabled) continue;

                    if (Vector2.DistanceSquared(t.WorldPosition, transducerCenter) < t.SoundRange * t.SoundRange)
                    {
                        Ping(t.WorldPosition, transducerCenter, 
                            t.SoundRange * passivePingRadius * 0.2f, t.SoundRange * prevPassivePingRadius * 0.2f, displayScale, t.SoundRange, 0.5f);
                        sonarBlips.Add(new SonarBlip(t.WorldPosition, 1.0f, 1.0f));
                    }
                }
            }
            prevPassivePingRadius = passivePingRadius;
        }
        
        private void DrawSonar(SpriteBatch spriteBatch, Rectangle rect)
        {
            displayBorderSize = 0.2f;
               center = new Vector2(rect.X + rect.Width * 0.5f, rect.Center.Y);
            displayRadius = (rect.Width / 2.0f) * (1.0f - displayBorderSize);
            displayScale = displayRadius / range;

            if (IsActive)
            {
                pingCircle.Draw(spriteBatch, center, Color.White * (1.0f - pingState), 0.0f, (displayRadius * 2 / pingCircle.size.X) * pingState);
            }

            float signalStrength = 1.0f;
            if (UseTransducers)
            {
                signalStrength = 0.0f;
                foreach (ConnectedTransducer connectedTransducer in connectedTransducers)
                {
                    signalStrength = Math.Max(signalStrength, connectedTransducer.SignalStrength);
                }
            }

            Vector2 transducerCenter = UseTransducers && connectedTransducers.Count > 0 ? GetTransducerCenter() : item.WorldPosition;

            if (item.Submarine != null && !DetectSubmarineWalls)
            {
                float simScale = displayScale * Physics.DisplayToSimRation;

                foreach (Submarine submarine in Submarine.Loaded)
                {
                    if (UseTransducers ?
                        !connectedTransducers.Any(t => submarine == t.Transducer.Item.Submarine || submarine.DockedTo.Contains(t.Transducer.Item.Submarine)) :
                        submarine != item.Submarine && !submarine.DockedTo.Contains(item.Submarine)) continue;
                    if (submarine.HullVertices == null) continue;

                    Vector2 offset = ConvertUnits.ToSimUnits(submarine.WorldPosition - transducerCenter);

                    for (int i = 0; i < submarine.HullVertices.Count; i++)
                    {
                        Vector2 start = (submarine.HullVertices[i] + offset) * simScale;
                        start.Y = -start.Y;
                        Vector2 end = (submarine.HullVertices[(i + 1) % submarine.HullVertices.Count] + offset) * simScale;
                        end.Y = -end.Y;

                        if (start.LengthSquared() > displayRadius * displayRadius ||
                            end.LengthSquared() > displayRadius * displayRadius) continue;

                        GUI.DrawLine(spriteBatch, center + start, center + end, Color.LightBlue * signalStrength, width: 3);
                    }
                }
            }

            if (sonarBlips.Count > 0)
            {
                spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive);

                foreach (SonarBlip sonarBlip in sonarBlips)
                {
                    DrawBlip(spriteBatch, sonarBlip, transducerCenter, center, sonarBlip.FadeTimer / 2.0f * signalStrength);
                }

                spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Immediate, null, null, null, GameMain.ScissorTestEnable);
            }

            if (GameMain.DebugDraw)
            {
                GUI.DrawString(spriteBatch, rect.Location.ToVector2(), sonarBlips.Count.ToString(), Color.White);
            }

            if (screenOverlay != null)
            {
                screenOverlay.Draw(spriteBatch, center, 0.0f, rect.Width / screenOverlay.size.X);
            }

            if (signalStrength <= 0.5f)
            {
                string signalWeakText = TextManager.Get(signalStrength <= 0.0f ? "SonarNoSignal" : "SonarSignalWeak");
                GUI.DrawString(spriteBatch, 
                    new Vector2(rect.Center.X, rect.Center.Y + rect.Height * 0.25f) - GUI.Font.MeasureString(signalWeakText) * 0.5f,
                    signalWeakText, Color.Red, Color.Black * 0.8f);
            }

            if (GameMain.GameSession == null) return;

            DrawMarker(spriteBatch,
                GameMain.GameSession.StartLocation.Name,
                (Level.Loaded.StartPosition - transducerCenter), displayScale, center, (rect.Width * 0.5f));

            DrawMarker(spriteBatch,
                GameMain.GameSession.EndLocation.Name,
                (Level.Loaded.EndPosition - transducerCenter), displayScale, center, (rect.Width * 0.5f));

            foreach (AITarget aiTarget in AITarget.List)
            {
                if (!aiTarget.Enabled) continue;
                if (string.IsNullOrEmpty(aiTarget.SonarLabel) || aiTarget.SoundRange <= 0.0f) continue;

                if (Vector2.DistanceSquared(aiTarget.WorldPosition, transducerCenter) < aiTarget.SoundRange * aiTarget.SoundRange)
                {
                    DrawMarker(spriteBatch,
                        aiTarget.SonarLabel,
                        aiTarget.WorldPosition - transducerCenter, displayScale, center, (rect.Width * 0.47f));
                }
            }
            
            if (GameMain.GameSession.Mission != null)
            {
                var mission = GameMain.GameSession.Mission;

                if (!string.IsNullOrWhiteSpace(mission.SonarLabel) && mission.SonarPosition != Vector2.Zero)
                {
                    DrawMarker(spriteBatch,
                        mission.SonarLabel,
                        mission.SonarPosition - transducerCenter, displayScale, center, (rect.Width * 0.47f));
                }
            }

            foreach (Submarine sub in Submarine.Loaded)
            {
                if (!sub.OnSonar) continue;
                if (UseTransducers ?
                    connectedTransducers.Any(t => sub == t.Transducer.Item.Submarine || sub.DockedTo.Contains(t.Transducer.Item.Submarine)) :
                    sub == item.Submarine && sub.DockedTo.Contains(item.Submarine)) continue;
                if (sub.WorldPosition.Y > Level.Loaded.Size.Y) continue;
                             
                DrawMarker(spriteBatch, sub.Name, sub.WorldPosition - transducerCenter, displayScale, center, (rect.Width * 0.45f));
            }

            if (!GameMain.DebugDraw) return;

            var steering = item.GetComponent<Steering>();
            if (steering == null || steering.SteeringPath == null) return;

            Vector2 prevPos = Vector2.Zero;

            foreach (WayPoint wp in steering.SteeringPath.Nodes)
            {
                Vector2 pos = (wp.Position - transducerCenter) * displayScale;
                if (pos.Length() > displayRadius) continue;

                pos.Y = -pos.Y;
                pos += center;

                GUI.DrawRectangle(spriteBatch, new Rectangle((int)pos.X - 3 / 2, (int)pos.Y - 3, 6, 6), (steering.SteeringPath.CurrentNode == wp) ? Color.LightGreen : Color.Green, false);

                if (prevPos != Vector2.Zero)
                {
                    GUI.DrawLine(spriteBatch, pos, prevPos, Color.Green);
                }

                prevPos = pos;
            }
        }

        private void UpdateDisruptions(Vector2 pingSource, float worldPingRadius, float worldPrevPingRadius)
        {
            float worldPingRadiusSqr = worldPingRadius * worldPingRadius;
            float worldPrevPingRadiusSqr = worldPrevPingRadius * worldPrevPingRadius;

            disruptedDirections.Clear();

            float searchRadius = Math.Min(range, worldPingRadius * 2);
            for (float x = pingSource.X - searchRadius; x < pingSource.X + searchRadius; x += Level.GridCellSize)
            {
                for (float y = pingSource.Y - searchRadius; y < pingSource.Y + searchRadius; y += Level.GridCellSize)
                {
                    Vector2 disruptionPos = new Vector2(
                        MathUtils.RoundTowardsClosest(x, Level.GridCellSize) + Level.GridCellSize / 2,
                        MathUtils.RoundTowardsClosest(y, Level.GridCellSize) + Level.GridCellSize / 2);

                    float disruptionStrength = Level.Loaded.GetSonarDisruptionStrength(disruptionPos);
                    if (disruptionStrength > 0.0f)
                    {
                        float disruptionDist = Vector2.Distance(pingSource, disruptionPos);
                        disruptedDirections.Add(new Pair<Vector2, float>((disruptionPos - pingSource) / disruptionDist, disruptionStrength));

                        if (disruptionDist > worldPrevPingRadius && disruptionDist <= worldPingRadius)
                        {
                            for (int i = 0; i < disruptionStrength * Level.GridCellSize * 0.02f; i++)
                            {
                                var blip = new SonarBlip(disruptionPos + Rand.Vector(Rand.Range(0.0f, Level.GridCellSize * 4 * disruptionStrength)), MathHelper.Lerp(1.0f, 1.5f, disruptionStrength), Rand.Range(1.0f, 2.0f + disruptionStrength));
                                sonarBlips.Add(blip);
                            }
                        }
                    }
                }
            }
        }

        private void Ping(Vector2 pingSource, Vector2 transducerPos, float pingRadius, float prevPingRadius, float displayScale, float range, float pingStrength = 1.0f)
        {
            float prevPingRadiusSqr = prevPingRadius * prevPingRadius;
            float pingRadiusSqr = pingRadius * pingRadius;
                        
            //inside a hull -> only show the edges of the hull
            if (item.CurrentHull != null && DetectSubmarineWalls)
            {
                CreateBlipsForLine(
                    new Vector2(item.CurrentHull.WorldRect.X, item.CurrentHull.WorldRect.Y), 
                    new Vector2(item.CurrentHull.WorldRect.Right, item.CurrentHull.WorldRect.Y), 
                    transducerPos,
                    pingRadius, prevPingRadius, 50.0f, 5.0f, range, 2.0f);

                CreateBlipsForLine(
                    new Vector2(item.CurrentHull.WorldRect.X, item.CurrentHull.WorldRect.Y - item.CurrentHull.Rect.Height),
                    new Vector2(item.CurrentHull.WorldRect.Right, item.CurrentHull.WorldRect.Y - item.CurrentHull.Rect.Height),
                    transducerPos,
                    pingRadius, prevPingRadius, 50.0f, 5.0f, range, 2.0f);

                CreateBlipsForLine(
                    new Vector2(item.CurrentHull.WorldRect.X, item.CurrentHull.WorldRect.Y),
                    new Vector2(item.CurrentHull.WorldRect.X, item.CurrentHull.WorldRect.Y - item.CurrentHull.Rect.Height),
                    transducerPos,
                    pingRadius, prevPingRadius, 50.0f, 5.0f, range, 2.0f);

                CreateBlipsForLine(
                    new Vector2(item.CurrentHull.WorldRect.Right, item.CurrentHull.WorldRect.Y),
                    new Vector2(item.CurrentHull.WorldRect.Right, item.CurrentHull.WorldRect.Y - item.CurrentHull.Rect.Height),
                    transducerPos,
                    pingRadius, prevPingRadius, 50.0f, 5.0f, range, 2.0f);

                return;
            }

            foreach (Submarine submarine in Submarine.Loaded)
            {
                if (submarine.HullVertices == null) continue;
                if (!DetectSubmarineWalls)
                {
                    if (UseTransducers)
                    {
                        if (connectedTransducers.Any(t => submarine == t.Transducer.Item.Submarine || 
                            submarine.DockedTo.Contains(t.Transducer.Item.Submarine))) continue;
                    }
                    else
                    {
                        if (item.Submarine == submarine) continue;
                        if (item.Submarine != null && item.Submarine.DockedTo.Contains(submarine)) continue;
                    }
                }

                for (int i = 0; i < submarine.HullVertices.Count; i++)
                {
                    Vector2 start = ConvertUnits.ToDisplayUnits(submarine.HullVertices[i]);
                    Vector2 end = ConvertUnits.ToDisplayUnits(submarine.HullVertices[(i + 1) % submarine.HullVertices.Count]);

                    if (item.Submarine == submarine)
                    {
                        start += Rand.Vector(500.0f);
                        end += Rand.Vector(500.0f);
                    }

                    CreateBlipsForLine(
                        start + submarine.WorldPosition,
                        end + submarine.WorldPosition,
                        transducerPos,
                        pingRadius, prevPingRadius,
                        200.0f, 2.0f, range, 1.0f);
                }
            }

            if (Level.Loaded != null && (item.CurrentHull == null || !DetectSubmarineWalls))
            {
                if (Level.Loaded.Size.Y - pingSource.Y < range)
                {
                    CreateBlipsForLine(
                        new Vector2(pingSource.X - range, Level.Loaded.Size.Y),
                        new Vector2(pingSource.X + range, Level.Loaded.Size.Y),
                        transducerPos,
                        pingRadius, prevPingRadius,
                        250.0f, 150.0f, range, pingStrength);
                }

                List<Voronoi2.VoronoiCell> cells = Level.Loaded.GetCells(pingSource, 7);
                foreach (Voronoi2.VoronoiCell cell in cells)
                {
                    foreach (Voronoi2.GraphEdge edge in cell.edges)
                    {
                        if (!edge.IsSolid) continue;
                        float cellDot = Vector2.Dot(cell.Center - pingSource, (edge.Center + cell.Translation) - cell.Center);
                        if (cellDot > 0) continue;

                        float facingDot = Vector2.Dot(
                            Vector2.Normalize(edge.Point1 - edge.Point2),
                            Vector2.Normalize(cell.Center - pingSource));

                        CreateBlipsForLine(
                            edge.Point1 + cell.Translation,
                            edge.Point2 + cell.Translation,
                            transducerPos,
                            pingRadius, prevPingRadius,
                            350.0f, 3.0f * (Math.Abs(facingDot) + 1.0f), range, pingStrength);
                    }
                }

                foreach (RuinGeneration.Ruin ruin in Level.Loaded.Ruins)
                {
                    if (!MathUtils.CircleIntersectsRectangle(pingSource, range, ruin.Area)) continue;

                    foreach (var ruinShape in ruin.RuinShapes)
                    {
                        foreach (RuinGeneration.Line wall in ruinShape.Walls)
                        {
                            float cellDot = Vector2.Dot(
                                Vector2.Normalize(ruinShape.Center - pingSource),
                                Vector2.Normalize((wall.A + wall.B) / 2.0f - ruinShape.Center));
                            if (cellDot > 0) continue;

                            CreateBlipsForLine(
                                wall.A, wall.B,
                                transducerPos,
                                pingRadius, prevPingRadius,
                                100.0f, 1000.0f, range, pingStrength);
                        }
                    }
                }
            }

            foreach (Item item in Item.ItemList)
            {
                if (item.CurrentHull == null && item.Prefab.SonarSize > 0.0f)
                {
                    float pointDist = ((item.WorldPosition - pingSource) * displayScale).LengthSquared();

                    if (pointDist > prevPingRadiusSqr && pointDist < pingRadiusSqr)
                    {
                        var blip = new SonarBlip(
                            item.WorldPosition + Rand.Vector(item.Prefab.SonarSize),
                            MathHelper.Clamp(item.Prefab.SonarSize, 0.1f, pingStrength),
                            MathHelper.Clamp(item.Prefab.SonarSize * 0.1f, 0.1f, 10.0f));
                        sonarBlips.Add(blip);
                    }
                }
            }

            foreach (Character c in Character.CharacterList)
            {
                if (c.AnimController.CurrentHull != null || !c.Enabled) continue;
                if (DetectSubmarineWalls && c.AnimController.CurrentHull == null && item.CurrentHull != null) continue;

                foreach (Limb limb in c.AnimController.Limbs)
                {
                    float pointDist = ((limb.WorldPosition - pingSource) * displayScale).LengthSquared();

                    if (limb.SimPosition == Vector2.Zero || pointDist > displayRadius * displayRadius) continue;

                    if (pointDist > prevPingRadiusSqr && pointDist < pingRadiusSqr)
                    {
                        var blip = new SonarBlip(
                            limb.WorldPosition + Rand.Vector(limb.Mass / 10.0f), 
                            MathHelper.Clamp(limb.Mass, 0.1f, pingStrength), 
                            MathHelper.Clamp(limb.Mass * 0.1f, 0.1f, 2.0f));
                        sonarBlips.Add(blip);
                    }
                }
            }
        }

        private void CreateBlipsForLine(Vector2 point1, Vector2 point2, Vector2 transducerPos, float pingRadius, float prevPingRadius,
            float lineStep, float zStep, float range, float pingStrength)
        {
            range *= displayScale;
            float length = (point1 - point2).Length();
            Vector2 lineDir = (point2 - point1) / length;
            for (float x = 0; x < length; x += lineStep * Rand.Range(0.8f, 1.2f))
            {
                Vector2 point = point1 + lineDir * x;
                //point += cell.Translation;

                Vector2 pointDiff = point - transducerPos;
                float pointDist = pointDiff.Length();
                float displayPointDist = pointDist * displayScale;

                if (displayPointDist > displayRadius) continue;
                if (displayPointDist < prevPingRadius || displayPointDist > pingRadius) continue;

                bool disrupted = false;
                foreach (Pair<Vector2, float> disruptDir in disruptedDirections)
                {
                    float dot = Vector2.Dot(pointDiff / pointDist, disruptDir.First);
                    if (dot >  1.0f - disruptDir.Second)
                    {
                        disrupted = true;
                        break;
                    }
                }
                if (disrupted) continue;

                float alpha = pingStrength * Rand.Range(1.5f, 2.0f);
                for (float z = 0; z < displayRadius - displayPointDist; z += zStep)
                {
                    Vector2 pos = point + Rand.Vector(150.0f) + Vector2.Normalize(point - item.WorldPosition) * z / displayScale;
                    float fadeTimer = alpha * (1.0f - displayPointDist / range);

                    int minDist = 200;
                    sonarBlips.RemoveAll(b => b.FadeTimer < fadeTimer && Math.Abs(pos.X - b.Position.X) < minDist && Math.Abs(pos.Y - b.Position.Y) < minDist);

                    var blip = new SonarBlip(pos, fadeTimer, 1.0f + ((displayPointDist + z) / displayRadius));

                    sonarBlips.Add(blip);
                    zStep += 0.5f;

                    if (z == 0)
                    {
                        alpha = Math.Min(alpha - 0.5f, 1.5f);
                    }
                    else
                    {
                        alpha -= 0.1f;
                    }

                    if (alpha < 0) break;
                }
            }
        }

        private void DrawBlip(SpriteBatch spriteBatch, SonarBlip blip, Vector2 transducerPos, Vector2 center, float strength)
        {
            strength = MathHelper.Clamp(strength, 0.0f, 1.0f);
            
            float scaledT = strength * (blipColorGradient.Length - 1);
            Color color = Color.Lerp(blipColorGradient[(int)scaledT], blipColorGradient[(int)Math.Min(scaledT + 1, blipColorGradient.Length - 1)], (scaledT - (int)scaledT));

            Vector2 pos = (blip.Position - transducerPos) * displayScale;
            pos.Y = -pos.Y;

            float posDist = pos.Length();
            if (posDist > displayRadius)
            {
                blip.FadeTimer = 0.0f;
                return;
            }

            if (sonarBlip == null)
            {
                GUI.DrawRectangle(spriteBatch, center + pos, Vector2.One * 4, Color.Magenta, true);
                return;
            }

            Vector2 dir = pos / posDist;

            Vector2 normal = new Vector2(dir.Y, -dir.X);
            float scale = (strength + 3.0f) * blip.Scale;

            sonarBlip.Draw(spriteBatch, center + pos, color, sonarBlip.Origin, blip.Rotation ?? MathUtils.VectorToAngle(pos),
                blip.Size * scale * 0.04f, SpriteEffects.None, 0);

            pos += Rand.Range(0.0f, 1.0f) * dir + Rand.Range(-scale, scale) * normal;

            sonarBlip.Draw(spriteBatch, center + pos, color * 0.5f, sonarBlip.Origin, 0, scale * 0.08f, SpriteEffects.None, 0);
        }

        private void DrawMarker(SpriteBatch spriteBatch, string label, Vector2 position, float scale, Vector2 center, float radius)
        {
            float dist = position.Length();

            position *= scale;
            position.Y = -position.Y;

            float textAlpha = MathHelper.Clamp(1.5f - dist / 50000.0f, 0.5f, 1.0f);

            Vector2 dir = Vector2.Normalize(position);
            Vector2 markerPos = (dist * scale > radius) ? dir * radius : position;
            markerPos += center;

            markerPos.X = (int)markerPos.X;
            markerPos.Y = (int)markerPos.Y;

            if (!GuiFrame.Children.First().Rect.Contains(markerPos))
            {
                Vector2? intersection = MathUtils.GetLineRectangleIntersection(center, markerPos, GuiFrame.Children.First().Rect);
                if (intersection.HasValue) markerPos = intersection.Value;                
            }

            GUI.DrawRectangle(spriteBatch, new Rectangle((int)markerPos.X, (int)markerPos.Y, 5, 5), Color.LightBlue);

            string wrappedLabel = ToolBox.WrapText(label, 150, GUI.SmallFont);
            wrappedLabel += "\n" + ((int)(dist * Physics.DisplayToRealWorldRatio) + " m");

            Vector2 labelPos = markerPos;
            Vector2 textSize = GUI.SmallFont.MeasureString(wrappedLabel);

            //flip the text to left side when the marker is on the left side or goes outside the right edge of the interface
            if (dir.X < 0.0f || labelPos.X + textSize.X + 10 > GuiFrame.Rect.X) labelPos.X -= textSize.X + 10;

            GUI.DrawString(spriteBatch,
                new Vector2(labelPos.X + 10, labelPos.Y),
                wrappedLabel,
                Color.LightBlue * textAlpha, Color.Black * textAlpha * 0.8f,
                2, GUI.SmallFont);
        }
        
        public void ClientWrite(Lidgren.Network.NetBuffer msg, object[] extraData = null)
        {
            msg.Write(IsActive);
        }
        
        public void ClientRead(ServerNetObject type, Lidgren.Network.NetBuffer msg, float sendingTime)
        {
            if (correctionTimer > 0.0f)
            {
                StartDelayedCorrection(type, msg.ExtractBits(1), sendingTime);
                return;
            }

            IsActive = msg.ReadBoolean();
            isActiveTickBox.Selected = IsActive;
        }
    }

    class SonarBlip
    {
        public float FadeTimer;
        public Vector2 Position;
        public float Scale;
        public Vector2 Velocity;
        public float? Rotation;
        public Vector2 Size;

        public SonarBlip(Vector2 pos, float fadeTimer, float scale)
        {
            Position = pos;
            FadeTimer = Math.Max(fadeTimer, 0.0f);
            Scale = scale;
            Size = new Vector2(0.5f, 1.0f);
        }
    }
}