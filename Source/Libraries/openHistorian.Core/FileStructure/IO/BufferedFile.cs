﻿//******************************************************************************************************
//  BufferedFile.cs - Gbtc
//
//  Copyright © 2013, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the Eclipse Public License -v 1.0 (the "License"); you may
//  not use this file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://www.opensource.org/licenses/eclipse-1.0.php
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  2/1/2013 - Steven E. Chisholm
//       Generated original version of source code. 
//       
//
//******************************************************************************************************

using System;
using System.IO;
using GSF;
using GSF.IO.Unmanaged;
using GSF.UnmanagedMemory;

namespace openHistorian.FileStructure.IO
{
    /// <summary>
    /// A buffered file stream utilizes the buffer pool to intellectually cache
    /// the contents of files.  
    /// </summary>
    /// <remarks>
    /// The cache algorithm is a least recently used algorithm.
    /// but will place more emphysis on object that are repeatidly accessed over 
    /// ones that are rarely accessed. This is accomplised by incrementing a counter
    /// every time a page is accessed and dividing by 2 every time a collection occurs from the buffer pool.
    /// </remarks>
    //ToDo: Consider allowing this class to scale horizontally like how the concurrent dictionary scales.
    //ToDo: this will reduce the concurrent contention on the class at the cost of more memory required.
    internal partial class BufferedFile : IDiskMediumCoreFunctions
    {
        #region [ Members ]


        /// <summary>
        /// Any uncommitted data resides in this location.
        /// </summary>
        private MemoryPoolStreamCore m_writeBuffer;

        /// <summary>
        /// The number of bytes contained in the committed area of the file.
        /// </summary>
        private long m_lengthOfCommittedData;

        /// <summary>
        /// The length of the 10 header pages. 
        /// </summary>
        private readonly long m_lengthOfHeader;

        /// <summary>
        /// Synchronize all calls to this class.
        /// </summary>
        private readonly object m_syncRoot;

        /// <summary>
        /// The <see cref="MemoryPool"/> where the provided data comes from.
        /// </summary>
        private readonly MemoryPool m_pool;

        /// <summary>
        /// The file stream use by this class.
        /// </summary>
        private FileStream m_baseStream;

        /// <summary>
        /// Location to store cached memory pages.
        /// All Calls to this class must be synchronized as this class is not thread safe.
        /// </summary>
        private PageReplacementAlgorithm m_pageReplacementAlgorithm;

        /// <summary>
        /// Gets if the class has been disposed.
        /// </summary>
        private bool m_disposed;

        /// <summary>
        /// All I/O to the disk is done at this maximum block size. Usually 64KB
        /// This value must be less than or equal to the MemoryPool's Buffer Size.
        /// </summary>
        private readonly int m_diskBlockSize;

        /// <summary>
        /// The size of an individual block of the FileStructure. Usually 4KB.
        /// </summary>
        private readonly int m_fileStructureBlockSize;

        /// <summary>
        /// Manages all I/O done to the physical file.
        /// </summary>
        private IoQueue m_queue;

        #endregion

        #region [ Constructors ]


        /// <summary>
        /// Creates a file backed memory stream.
        /// </summary>
        /// <param name="stream">The <see cref="FileStream"/> to buffer</param>
        /// <param name="pool">The <see cref="MemoryPool"/> to allocate memory from</param>
        /// <param name="header">The <see cref="FileHeaderBlock"/> to be managed when modifications occur</param>
        /// <param name="isNewFile">Tells if this is a newly created file. This will make sure that the 
        /// first 10 pages have the header data copied to it.</param>
        public BufferedFile(FileStream stream, MemoryPool pool, FileHeaderBlock header, bool isNewFile)
        {
            m_fileStructureBlockSize = header.BlockSize;
            m_diskBlockSize = pool.PageSize;
            m_lengthOfHeader = header.BlockSize * 10;
            m_writeBuffer = new MemoryPoolStreamCore(pool);
            m_pool = pool;
            m_queue = new IoQueue(stream, pool.PageSize, header.BlockSize);
            m_syncRoot = new object();
            m_pageReplacementAlgorithm = new PageReplacementAlgorithm(pool);
            m_baseStream = stream;
            pool.RequestCollection += m_pool_RequestCollection;

            if (isNewFile)
            {
                byte[] headerBytes = header.GetBytes();
                stream.Position = 0;
                for (int x = 0; x < 10; x++)
                {
                    stream.Write(headerBytes, 0, headerBytes.Length);
                }
            }
            m_lengthOfCommittedData = (header.LastAllocatedBlock + 1) * (long)header.BlockSize;
            m_writeBuffer.ConfigureAlignment(m_lengthOfCommittedData, pool.PageSize);
        }

        #endregion

        #region [ Properties ]

        /// <summary>
        /// Gets the current number of bytes used by the file system. 
        /// This is only intended to be an approximate figure. 
        /// </summary>
        public long Length
        {
            get
            {
                return m_baseStream.Length;
            }
        }

        #endregion

        #region [ Public Methdos ] 

        /// <summary>
        /// Executes a commit of data. This will flush the data to the disk use the provided header data to properly
        /// execute this function.
        /// </summary>
        /// <param name="header"></param>
        public void FlushWithHeader(FileHeaderBlock header)
        {
            //Determine how much committed data to write
            long lengthOfAllData = (header.LastAllocatedBlock + 1) * (long)m_fileStructureBlockSize;
            long copyLength = lengthOfAllData - m_lengthOfCommittedData;

            //Write the uncommitted data.
            m_queue.Write(m_writeBuffer, m_lengthOfCommittedData, copyLength, waitForWriteToDisk: true);

            //Update the new header to position 0, position 1, and one of position 2-9
            byte[] bytes = header.GetBytes();
            m_queue.Write(0, bytes, m_fileStructureBlockSize);
            m_queue.Write(m_fileStructureBlockSize, bytes, m_fileStructureBlockSize);
            m_queue.Write(m_fileStructureBlockSize * ((header.SnapshotSequenceNumber & 7) + 2), bytes, m_fileStructureBlockSize);
            m_queue.FlushFileBuffers();

            long startPos;

            //Copy recently committed data to the buffer pool
            if ((m_lengthOfCommittedData & (m_diskBlockSize - 1)) != 0) //Only if there is a split page.
            {
                startPos = m_lengthOfCommittedData & (~(long)(m_diskBlockSize - 1));
                //Finish filling up the split page in the buffer.
                lock (m_syncRoot)
                {
                    IntPtr ptrDest;

                    if (m_pageReplacementAlgorithm.TryGetSubPageNoLock(startPos, out ptrDest))
                    {
                        int length;
                        IntPtr ptrSrc;
                        m_writeBuffer.ReadBlock(m_lengthOfCommittedData, out ptrSrc, out length);
                        Footer.WriteChecksumResultsToFooter(ptrSrc, m_fileStructureBlockSize, length);
                        ptrDest += (m_diskBlockSize - length);
                        Memory.Copy(ptrSrc, ptrDest, length);
                    }
                }
                startPos += m_diskBlockSize;
            }
            else
            {
                startPos = m_lengthOfCommittedData;
            }

            while (startPos < lengthOfAllData)
            {
                //If the address doesn't exist in the current list. Read it from the disk.
                int poolPageIndex;
                IntPtr poolAddress;
                m_pool.AllocatePage(out poolPageIndex, out poolAddress);
                m_writeBuffer.CopyTo(startPos, poolAddress, m_diskBlockSize);
                Footer.WriteChecksumResultsToFooter(poolAddress, m_fileStructureBlockSize, m_diskBlockSize);

                bool wasPageAdded;
                lock (m_syncRoot)
                {
                    wasPageAdded = m_pageReplacementAlgorithm.TryAddPage(startPos, poolAddress, poolPageIndex);
                }
                if (!wasPageAdded)
                    m_pool.ReleasePage(poolPageIndex);

                startPos += m_diskBlockSize;
            }

            m_lengthOfCommittedData = lengthOfAllData;
            m_writeBuffer.ConfigureAlignment(m_lengthOfCommittedData, m_pool.PageSize);
        }

        /// <summary>
        /// Creates a <see cref="BinaryStreamIoSessionBase"/> that can be used to read from this disk medium.
        /// </summary>
        /// <returns></returns>
        public BinaryStreamIoSessionBase CreateIoSession()
        {
            lock (m_syncRoot)
            {
                return new IoSession(this, m_pageReplacementAlgorithm.GetPageLock());
            }
        }

        /// <summary>
        /// Rolls back all edits to the DiskMedium
        /// </summary>
        public void RollbackChanges()
        {
            if (m_disposed)
                throw new ObjectDisposedException(GetType().ToString());
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            if (!m_disposed)
            {
                try
                {
                    m_disposed = true;
                    //Unregistering from this event gaurentees that a collection will no longer
                    //be called since this class utilizes custom code to garentee this.
                    Globals.MemoryPool.RequestCollection -= m_pool_RequestCollection;

                    lock (m_syncRoot)
                    {
                        m_pageReplacementAlgorithm.Dispose();
                        m_baseStream.Dispose();
                        m_writeBuffer.Dispose();
                    }
                }
                finally
                {
                    m_baseStream = null;
                    m_disposed = true;
                    m_pageReplacementAlgorithm = null;
                    m_writeBuffer = null;
                    m_queue = null;
                }
            }
        }

        #endregion

        #region [ Private Methods ]


        /// <summary>
        /// Populates the pointer data inside <see cref="args"/> for the desired block as specified in <see cref="args"/>.
        /// This function will block if needing to retrieve data from the disk.
        /// </summary>
        /// <param name="pageLock">The reusable lock information about what this block is currently using.</param>
        /// <param name="args">Contains what block needs to be read and when this function returns, 
        /// it will contain the proper pointer information for this block.</param>
        private void GetBlock(PageLock pageLock, BlockArguments args)
        {
            pageLock.Clear();
            //Determines where the block is located.
            if (args.Position >= m_lengthOfCommittedData)
            {
                //If the block is in the uncommitted space, it is stored in the 
                //MemoryPoolStreamCore.
                args.SupportsWriting = true;
                m_writeBuffer.GetBlock(args);
            }
            else if (args.Position < m_lengthOfHeader)
            {
                //If the block is in the header, error because this area of the file is not designed to be accessed.
                throw new ArgumentOutOfRangeException("args", "Cannot use this function to modify the file header.");
            }
            else
            {
                //If it is between the file header and uncommitted space, 
                //it is in the committed space, which this space by design is never to be modified. 
                if (args.IsWriting)
                    throw new ArgumentException("Cannot write to committed data space", "args");
                args.SupportsWriting = false;
                args.Length = m_diskBlockSize;
                //rounds to the beginning of the block to be looked up.
                args.FirstPosition = args.Position & ~(long)m_pool.PageMask;

                GetBlockFromCommittedSpace(pageLock, args.FirstPosition, out args.FirstPointer);

                //Make sure the block does not go beyond the end of the uncommitted space.
                if (args.FirstPosition + args.Length > m_lengthOfCommittedData)
                    args.Length = (int)(m_lengthOfCommittedData - args.FirstPosition);
            }
        }

        /// <summary>
        /// Processes the GetBlock from the committed area.
        /// </summary>
        /// <param name="pageLock"></param>
        /// <param name="position"></param>
        /// <param name="pointer">an output parameter that contains the pointer for the provided position</param>
        /// <remarks>The valid length is at least the size of the buffer pools page size.</remarks>
        private void GetBlockFromCommittedSpace(PageLock pageLock, long position, out IntPtr pointer)
        {
            lock (m_syncRoot)
            {
                //If the page is in the buffer, we can return and don't have to read it.
                if (m_pageReplacementAlgorithm.TryGetSubPage(pageLock, position, out pointer))
                    return;
            }

            //If the address doesn't exist in the current list. Read it from the disk.
            int poolPageIndex;
            IntPtr poolAddress;
            m_pool.AllocatePage(out poolPageIndex, out poolAddress);

            m_queue.Read(position, poolAddress);

            //Since a race condition exists, I need to check the buffer to make sure that 
            //the most recently read page already exists in the PageReplacementAlgorithm.
            bool wasPageAdded;
            lock (m_syncRoot)
            {
                pointer = m_pageReplacementAlgorithm.AddOrGetPage(pageLock, position, poolAddress, poolPageIndex, out wasPageAdded);
            }
            //If I lost on the race condition, I need to re-release this page.
            if (!wasPageAdded)
                m_pool.ReleasePage(poolPageIndex);
        }

        /// <summary>
        /// Handles the <see cref="MemoryPool.RequestCollection"/> event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void m_pool_RequestCollection(object sender, CollectionEventArgs e)
        {
            if (m_disposed)
                return;

            lock (m_syncRoot)
            {
                if (m_disposed)
                    return;
                m_pageReplacementAlgorithm.DoCollection(e);
            }

            if (e.CollectionMode == BufferPoolCollectionMode.Critical)
            {
                //ToDo: actually do something differently if collection level reaches critical
                lock (m_syncRoot)
                {
                    if (m_disposed)
                        return;
                    m_pageReplacementAlgorithm.DoCollection(e);
                }
            }
        }

        #endregion


    }
}