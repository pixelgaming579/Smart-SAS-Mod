using UnityEngine;
using System;
using HarmonyLib;
using SFS.World;
using SFS.UI;
using SFS.World.Maps;
using System.Linq;
using System.Reflection;
using ModLoader;

namespace SmartSASMod
{
    [HarmonyPatch(typeof(Rocket), "GetStopRotationTurnAxis")]
    class CustomSAS
    {
        private static float _lastAngularVelocity=0;
        private static float _lastDeltaAngle=0;
        private static bool _lastValuesKnown=false;

        static float Postfix(float result, Rocket __instance)
        {
            SASComponent sas = __instance.GetOrAddComponent<SASComponent>();
            __instance.rb2d.angularDrag = 0.05f;

            if (!WorldTime.main.realtimePhysics.Value || !__instance.hasControl.Value)
                return result;

            float angularVelocity = __instance.rb2d.angularVelocity;
            float angleOffset = GUI.GetAngleOffsetFloat();
            float currentRotation = GUI.NormaliseAngle(__instance.GetRotation());

            float TargetRotationToTorque(float rot)
            {
                float deltaAngle = GUI.NormaliseAngle(currentRotation - (rot - angleOffset));
                if (deltaAngle > 180)
                {
                    deltaAngle -= 360;
                }
                else if (deltaAngle < -180)
                    deltaAngle += 360;
                float o = -Mathf.Sign(-angularVelocity - (Mathf.Sign(deltaAngle) * (25 - (25 * 15 / (Mathf.Pow(Mathf.Abs(deltaAngle), 1.5f) + 15)))));

                if (_lastValuesKnown && Mathf.Abs(deltaAngle)>1 && Mathf.Abs(angularVelocity)>1e-3 && Mathf.Abs(deltaAngle-_lastDeltaAngle)>1e-3)
                {
                    double estimatedTimeInterval=Math.Abs((deltaAngle-_lastDeltaAngle)/angularVelocity);
                    double angularAcceleration=Math.Abs(angularVelocity-_lastAngularVelocity)/estimatedTimeInterval;

                    if (angularAcceleration>1e-3 && Math.Abs(deltaAngle)<2*angularVelocity*angularVelocity/angularAcceleration)
                    {
                        // too fast slow down (allowing for drag?)
                        o=-o/2;
                    }
                }
                _lastAngularVelocity=angularVelocity;
                _lastDeltaAngle=deltaAngle;
                _lastValuesKnown=true;

                return Mathf.Abs(deltaAngle) > 5 ? o : Mathf.Abs(deltaAngle) > 0.05f ? o / 2 : result;

                // TODO: Create a PID controller.
            }

            float targetRotation;
            switch (sas.currentDirection)
            {
                case DirectionMode.Default:
                    return result;

                case DirectionMode.Prograde:
                    Double2 offset = __instance.location.velocity.Value;
                    if (offset.magnitude <= 3)
                        return result;
                    return TargetRotationToTorque((float)Math.Atan2(offset.y, offset.x) * Mathf.Rad2Deg);

                case DirectionMode.Target:
                    Rocket rocket = PlayerController.main.player.Value as Rocket;
                    try
                    {
                        SelectableObject target = __instance == rocket ? Map.navigation.target : sas.previousTarget; // Keeps the last selected target if the sas comp.'s rocket isn't the currently controlled rocket.
                        if (Main.ANAISTraverse is Traverse traverse && __instance == rocket)
                        {
                            if (traverse.Field("_navState").GetValue().ToString() == "ANAIS_TRANSFER_PLANNED")
                            {
                                Double2 dv = traverse.Field<Double2>("_relativeVelocity").Value;
                                targetRotation = GUI.NormaliseAngle((float)Math.Atan2(dv.y, dv.x) * Mathf.Rad2Deg);
                                if (target != sas.previousTarget)
                                {
                                    MsgDrawer.main.Log("Using ANAIS navigation to " + target.Select_DisplayName);
                                    sas.previousTarget = target;
                                }
                                return TargetRotationToTorque(targetRotation);
                            }
                        }

                        if (target is MapRocket)
                        {
                            if (target != sas.previousTarget)
                            {
                                MsgDrawer.main.Log("Targeting " + (target as MapRocket).Select_DisplayName);
                                sas.previousTarget = target;
                            }
                            Vector2 targetOffset =
                                (target as MapRocket).rocket.location.Value.GetSolarSystemPosition((WorldTime.main != null) ? WorldTime.main.worldTime : 0.0)
                                    + (Vector2)(target as MapRocket).rocket.rb2d.transform.TransformVector((target as MapRocket).rocket.mass.GetCenterOfMass())
                                - (__instance.location.Value.GetSolarSystemPosition((WorldTime.main != null) ? WorldTime.main.worldTime : 0.0)
                                    + (Vector2)__instance.rb2d.transform.TransformVector(__instance.mass.GetCenterOfMass()));

                            return TargetRotationToTorque(Mathf.Atan2(targetOffset.y, targetOffset.x) * Mathf.Rad2Deg);
                        }
                        else if (target is MapPlanet)
                        {
                            if (target != sas.previousTarget)
                            {
                                MsgDrawer.main.Log("Targeting " + (target as MapPlanet).planet.DisplayName.GetSub(0));
                                sas.previousTarget = target;
                            }
                            Double2 currentPos = __instance.location.planet.Value.GetSolarSystemPosition() + __instance.location.position.Value + Double2.ToDouble2(__instance.rb2d.transform.TransformVector(__instance.mass.GetCenterOfMass()));
                            Double2 targetOffset = (target as MapPlanet).planet.GetSolarSystemPosition() - currentPos;
                            targetRotation = GUI.NormaliseAngle((float)Math.Atan2(targetOffset.y, targetOffset.x) * Mathf.Rad2Deg);
                            return TargetRotationToTorque(targetRotation);
                        }
                        else
                        {
                            if (rocket == __instance)
                            {
                                if (target == null)
                                {
                                    MsgDrawer.main.Log("No target selected, switching to default SAS");
                                }
                                else
                                {
                                    MsgDrawer.main.Log("Not a valid target, switching to default SAS");
                                }
                            }
                            sas.currentDirection = DirectionMode.Default;
                            return result;
                        }
                    }
                    catch (NullReferenceException)
                    {
                        if (rocket == __instance)
                            MsgDrawer.main.Log("No target selected, switching to default SAS");
                        sas.currentDirection = DirectionMode.Default;
                        return result;
                    }

                case DirectionMode.Surface:
                    targetRotation = GUI.NormaliseAngle((float)Math.Atan2(__instance.location.position.Value.y, __instance.location.position.Value.x) * Mathf.Rad2Deg);
                    return TargetRotationToTorque(targetRotation);

                case DirectionMode.None:
                    __instance.rb2d.angularDrag = 0;
                    return 0;
            }

            return result;
        }

    }
}