﻿/*
Ferram Aerospace Research v0.15.11.4 "Mach"
=========================
Aerodynamics model for Kerbal Space Program

Copyright 2019, Michael Ferrara, aka Ferram4

   This file is part of Ferram Aerospace Research.

   Ferram Aerospace Research is free software: you can redistribute it and/or modify
   it under the terms of the GNU General Public License as published by
   the Free Software Foundation, either version 3 of the License, or
   (at your option) any later version.

   Ferram Aerospace Research is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
   GNU General Public License for more details.

   You should have received a copy of the GNU General Public License
   along with Ferram Aerospace Research.  If not, see <http://www.gnu.org/licenses/>.

   Serious thanks:		a.g., for tons of bugfixes and code-refactorings
				stupid_chris, for the RealChuteLite implementation
            			Taverius, for correcting a ton of incorrect values
				Tetryds, for finding lots of bugs and issues and not letting me get away with them, and work on example crafts
            			sarbian, for refactoring code for working with MechJeb, and the Module Manager updates
            			ialdabaoth (who is awesome), who originally created Module Manager
                        	Regex, for adding RPM support
				DaMichel, for some ferramGraph updates and some control surface-related features
            			Duxwing, for copy editing the readme

   CompatibilityChecker by Majiir, BSD 2-clause http://opensource.org/licenses/BSD-2-Clause

   Part.cfg changes powered by sarbian & ialdabaoth's ModuleManager plugin; used with permission
	http://forum.kerbalspaceprogram.com/threads/55219

   ModularFLightIntegrator by Sarbian, Starwaster and Ferram4, MIT: http://opensource.org/licenses/MIT
	http://forum.kerbalspaceprogram.com/threads/118088

   Toolbar integration powered by blizzy78's Toolbar plugin; used with permission
	http://forum.kerbalspaceprogram.com/threads/60863
 */

using System;
using FerramAerospaceResearch.Settings;
using ModularFI;
using UnityEngine;

namespace FerramAerospaceResearch.FARAeroComponents
{
    [KSPAddon(KSPAddon.Startup.SpaceCentre, true)]
    public class ModularFlightIntegratorRegisterer : MonoBehaviour
    {
        private void Start()
        {
            FARLogger.Info("Modular Flight Integrator function registration started");
            ModularFlightIntegrator.RegisterUpdateAerodynamicsOverride(UpdateAerodynamics);
            ModularFlightIntegrator.RegisterUpdateThermodynamicsPre(UpdateThermodynamicsPre);
            ModularFlightIntegrator.RegisterCalculateAreaExposedOverride(CalculateAreaExposed);
            ModularFlightIntegrator.RegisterCalculateAreaRadiativeOverride(CalculateAreaRadiative);
            ModularFlightIntegrator.RegisterGetSunAreaOverride(CalculateSunArea);
            ModularFlightIntegrator.RegisterGetBodyAreaOverride(CalculateBodyArea);
            FARLogger.Info("Modular Flight Integrator function registration complete");
            Destroy(this);
        }

        private static void UpdateThermodynamicsPre(ModularFlightIntegrator fi)
        {
            for (int i = 0; i < fi.PartThermalDataCount; i++)
            {
                PartThermalData ptd = fi.partThermalDataList[i];
                Part part = ptd.part;
                if (!part.Modules.Contains<FARAeroPartModule>())
                    continue;

                FARAeroPartModule aeroModule = part.Modules.GetModule<FARAeroPartModule>();

                part.radiativeArea = CalculateAreaRadiative(fi, part, aeroModule);
                part.exposedArea =
                    part.machNumber > 0 ? CalculateAreaExposed(fi, part, aeroModule) : part.radiativeArea;

                if (FARSettings.ExposedAreaLimited && part.exposedArea > part.radiativeArea)
                    part.exposedArea = part.radiativeArea; //sanity check just in case
            }
        }

        private static void UpdateAerodynamics(ModularFlightIntegrator fi, Part part)
        {
            //FIXME Proper model for airbrakes
            if (part.Modules.Contains<ModuleAeroSurface>() ||
                part.Modules.Contains("MissileLauncher") && part.vessel.rootPart == part)
            {
                fi.BaseFIUpdateAerodynamics(part);
            }
            else
            {
                Rigidbody rb = part.rb;
                if (!rb)
                    return;
                part.dragVector = rb.velocity +
                                  Krakensbane.GetFrameVelocity() -
                                  FARWind.GetWind(FlightGlobals.currentMainBody, part, rb.position);
                part.dragVectorSqrMag = part.dragVector.sqrMagnitude;
                if (part.dragVectorSqrMag.NearlyEqual(0) || part.ShieldedFromAirstream)
                {
                    part.dragVectorMag = 0f;
                    part.dragVectorDir = Vector3.zero;
                    part.dragVectorDirLocal = Vector3.zero;
                    part.dragScalar = 0f;
                }
                else
                {
                    part.dragVectorMag = (float)Math.Sqrt(part.dragVectorSqrMag);
                    part.dragVectorDir = part.dragVector / part.dragVectorMag;
                    part.dragVectorDirLocal = -part.partTransform.InverseTransformDirection(part.dragVectorDir);
                    CalculateLocalDynPresAndAngularDrag(fi, part);
                }

                if (!part.DragCubes.None)
                    part.DragCubes.SetDrag(part.dragVectorDirLocal, (float)fi.mach);
            }
        }

        private static void CalculateLocalDynPresAndAngularDrag(ModularFlightIntegrator fi, Part p)
        {
            if (fi.CurrentMainBody.ocean && p.submergedPortion > 0)
            {
                p.submergedDynamicPressurekPa = fi.CurrentMainBody.oceanDensity * 1000;
                p.dynamicPressurekPa = p.atmDensity;
            }
            else
            {
                p.submergedDynamicPressurekPa = 0;
                p.dynamicPressurekPa = p.atmDensity;
            }

            double tmp = 0.0005 * p.dragVectorSqrMag;

            p.submergedDynamicPressurekPa *= tmp;
            p.dynamicPressurekPa *= tmp;

            tmp = p.dynamicPressurekPa * (1.0 - p.submergedPortion);
            tmp += p.submergedDynamicPressurekPa *
                   PhysicsGlobals.BuoyancyWaterAngularDragScalar *
                   p.waterAngularDragMultiplier *
                   p.submergedPortion;

            p.rb.angularDrag = (float)(p.angularDrag * tmp * PhysicsGlobals.AngularDragMultiplier);

            tmp = Math.Max(fi.pseudoReDragMult, 1);
            //dyn pres adjusted for submersion
            p.dragScalar = (float)((p.dynamicPressurekPa * (1.0 - p.submergedPortion) +
                                    p.submergedDynamicPressurekPa * p.submergedPortion) *
                                   tmp);
            p.bodyLiftScalar = (float)(p.dynamicPressurekPa * (1.0 - p.submergedPortion) +
                                       p.submergedDynamicPressurekPa * p.submergedPortion);
        }

        private static double CalculateAreaRadiative(ModularFlightIntegrator fi, Part part)
        {
            FARAeroPartModule module = null;
            if (part.Modules.Contains<FARAeroPartModule>())
                module = part.Modules.GetModule<FARAeroPartModule>();

            return CalculateAreaRadiative(fi, part, module);
        }

        private static double CalculateAreaRadiative(
            ModularFlightIntegrator fi,
            Part part,
            FARAeroPartModule aeroModule
        )
        {
            if (aeroModule is null)
                return fi.BaseFICalculateAreaRadiative(part);
            double radArea = aeroModule.ProjectedAreas.totalArea;

            return radArea > 0 ? radArea : fi.BaseFICalculateAreaRadiative(part);
        }

        private static double CalculateAreaExposed(ModularFlightIntegrator fi, Part part)
        {
            FARAeroPartModule module = null;
            if (part.Modules.Contains<FARAeroPartModule>())
                module = part.Modules.GetModule<FARAeroPartModule>();

            return CalculateAreaExposed(fi, part, module);
        }

        private static double CalculateAreaExposed(ModularFlightIntegrator fi, Part part, FARAeroPartModule aeroModule)
        {
            if (aeroModule is null)
                return fi.BaseFICalculateAreaExposed(part);

            double exposedArea = 0;
            if (FARSettings.ExposedAreaUsesKSPHack)
            {
                FARAeroPartModule.ProjectedArea areas = aeroModule.ProjectedAreas;

                // blame Squad for whatever this is, apparently exposed area is not base on geometry only...
                // without these... corrections convection heating is far lower than in stock and parts don't heat up as much
                float multiplier = 1f / part.DragCubes.DragCurveMultiplier.Evaluate((float)fi.mach);
                for (int i = 0; i < 6; i++)
                {
                    float dot = Vector3.Dot(part.DragCubes.DragVector,
                                              FARAeroPartModule.ProjectedArea.FaceDirections[i]);
                    float dragValue =
                        PhysicsGlobals.DragCurveValue(part.DragCubes.SurfaceCurves,
                                                      ((dot + 1.0f) * 0.5f),
                                                      (float)fi.mach);
                    double hackArea = areas[i] * dragValue;
                    float weight = part.DragCubes.WeightedDrag[i];

                    float areaWeight;
                    if (weight > 0.01 && weight < 1.0)
                        areaWeight = 1f / weight;
                    else
                        areaWeight = 1f;

                    exposedArea += hackArea * multiplier * areaWeight;
                }
            }
            else
            {
                exposedArea = aeroModule.ProjectedAreaLocal(-part.dragVectorDirLocal);
            }

            return exposedArea > 0 ? exposedArea : fi.BaseFICalculateAreaExposed(part);
        }

        private static double CalculateSunArea(ModularFlightIntegrator fi, PartThermalData ptd)
        {
            FARAeroPartModule module = null;
            if (ptd.part.Modules.Contains<FARAeroPartModule>())
                module = ptd.part.Modules.GetModule<FARAeroPartModule>();

            if (module is null)
                return fi.BaseFIGetSunArea(ptd);
            double sunArea = module.ProjectedAreaWorld(fi.sunVector) * ptd.sunAreaMultiplier;

            return sunArea > 0 ? sunArea : fi.BaseFIGetSunArea(ptd);
        }

        private static double CalculateBodyArea(ModularFlightIntegrator fi, PartThermalData ptd)
        {
            FARAeroPartModule module = null;
            if (ptd.part.Modules.Contains<FARAeroPartModule>())
                module = ptd.part.Modules.GetModule<FARAeroPartModule>();

            if (module is null)
                return fi.BaseFIBodyArea(ptd);
            double bodyArea = module.ProjectedAreaWorld(-fi.Vessel.upAxis) * ptd.bodyAreaMultiplier;

            return bodyArea > 0 ? bodyArea : fi.BaseFIBodyArea(ptd);
        }
    }
}
