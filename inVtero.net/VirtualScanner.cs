﻿// Shane.Macaulay @IOActive.com Copyright (C) 2013-2015

//Copyright(C) 2015 Shane Macaulay

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

// Shane.Macaulay@IOActive.com (c) copyright 2014,2015,2016 all rights reserved. GNU GPL License

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Reloc;
using System.IO;
using System.Collections.Concurrent;
using RaptorDB;
using ProtoBuf;

namespace inVtero.net
{
    /// <summary>
    /// Similar to the file/raw input version, but were now scanning Virtual Spaces
    /// </summary>
    [ProtoContract(AsReferenceDefault = true, ImplicitFields = ImplicitFields.AllPublic)]
    public class VirtualScanner
    {
        Mem BackingBlocks;
        //ConcurrentDictionary<int, Mem> MemoryBank;
        public ConcurrentDictionary<long, Extract> Artifacts;
        PhysicalMemoryStream BackingStream;

        /// <summary>
        /// Detected processes are the fundamental unit were after since a process maintains the address
        /// space configuration information.  Naturally there can be (bad)things running outside of this context
        /// in hardware, process-less threads/co-routines or through the interaction of some shared memory
        /// between several processes causes a Turing complete mechanism weird-machine...
        /// 
        /// But I think we have to start somewhere ;) 
        /// </summary>
        DetectedProc DPContext;

        WAHBitArray ScanList;

        // similar to the file based interface, we will only scan 1 page at a time 
        // but if we hit a signature in a check it's free to non-block the others while it completes 
        // a deeper scan
        List<Func<long, long[], VAScanType>> CheckMethods;

        VAScanType scanMode;
        public VAScanType ScanMode
        {
            get { return scanMode; }
            set
            {
                scanMode = value;
                CheckMethods.Clear();

                if ((value & VAScanType.PE_FAST) == VAScanType.PE_FAST)
                    CheckMethods.Add(FastPE);
            }
        }

        public VirtualScanner()
        {
            Artifacts = new ConcurrentDictionary<long, Extract>();
            CheckMethods = new List<Func<long, long[], VAScanType>>();
            ScanList = new WAHBitArray(); 
        }

        public VirtualScanner(DetectedProc Ctx, Mem backingBlocks) : this()
        {
            DPContext = Ctx;
            BackingBlocks = backingBlocks;
            BackingStream = new PhysicalMemoryStream(backingBlocks, Ctx);
        }

        public VAScanType FastPE(long VA, long[] Block)
        {
            // magic check before copying into byte[]
            uint PEMagic = (uint)Block[0] & 0xffff;
            if (PEMagic == 0x5a4d)
            {
                var bpage = new byte[0x1000];
                Buffer.BlockCopy(Block, 0, bpage, 0, 4096);
                // TODO: improve/fix check
                var extracted = Extract.IsBlockaPE(bpage);
                if (extracted != null)
                {
                    Artifacts.TryAdd(VA, extracted);
                    return VAScanType.PE_FAST;
                }
            }
            return VAScanType.UNDETERMINED;
        }

        /// <summary>
        /// Scan and return Extract objects which represent detected PE's
        /// </summary>
        /// <param name="Start"></param>
        /// <param name="Stop">We just truncate VA's at 48 bit's</param>
        /// <returns>count of new detections since last Run</returns>
        public Dictionary<long, Extract> Run(long Start=0, long Stop = 0xFFFFffffFFFF, ParallelLoopState pState = null, Mem Instance = null)
        {
            var memAxss = Instance == null ? BackingBlocks : Instance;

            var rv = new Dictionary<long, Extract>();
            // convert index to an address 
            // then add start to it
            long i = Start;
            var block = new long[0x200]; // 0x200 * 8 = 4k
            while (i < Stop)
            {

                if (pState != null && pState.IsStopped)
                    return rv;

                HARDWARE_ADDRESS_ENTRY locPhys = HARDWARE_ADDRESS_ENTRY.MinAddr;
                if (DPContext.vmcs != null)
                    locPhys = memAxss.VirtualToPhysical(DPContext.vmcs.EPTP, DPContext.CR3Value, i);
                else
                    locPhys = memAxss.VirtualToPhysical(DPContext.CR3Value, i);

                var Curr = i;
                i += 0x1000;

                if (HARDWARE_ADDRESS_ENTRY.IsBadEntry(locPhys))
                    continue;

                bool GotData = false;
                memAxss.GetPageForPhysAddr(locPhys, ref block, ref GotData);
                if (!GotData)
                    continue;

                foreach (var scanner in CheckMethods)
                {
                    var scan_detect = scanner(Curr, block);
                    if (scan_detect != VAScanType.UNDETERMINED)
                    {
                        rv.Add(Curr, Artifacts[Curr]);
                        if (Vtero.VerboseOutput)
                            Console.WriteLine($"Detected PE @ VA {Curr:X}");
                    }
                }
            }
            return rv;
        }
    }
}
