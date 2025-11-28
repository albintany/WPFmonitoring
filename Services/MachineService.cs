using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using MonitoringApp.ViewModels;

namespace MonitoringApp.Services
{
    public class MachineService
    {
        private readonly DatabaseService _db;

        private static readonly Dictionary<int, (string Name, string Line, string Process)> _machineCache = new();

        public MachineService()
        {
            _db = new DatabaseService();
        }


        public (string Name, string Line, string Process) GetMachineInfoCached(int id)
        {
            // A. Cek Cache Dulu (In-Memory)
            if (_machineCache.ContainsKey(id))
            {
                return _machineCache[id];
            }

            // B. Jika Tidak Ada, Ambil dari Database
            string name = "Unknown", line = "-", process = "-";
            
            using (var conn = _db.GetConnection())
            {
                try
                {
                    conn.Open();
                    var cmd = new SqlCommand("SELECT name, line_production, process FROM line WHERE id = @id", conn);
                    cmd.Parameters.AddWithValue("@id", id);
                    
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            name = r["name"] != DBNull.Value ? r["name"].ToString() : "Unknown";
                            line = r["line_production"] != DBNull.Value ? r["line_production"].ToString() : "-";
                            process = r["process"] != DBNull.Value ? r["process"].ToString() : "-";
                        }
                    }
                }
                catch { /* Ignore error, return default */ }
            }

            // C. Simpan ke Cache
            var result = (name, line, process);
            _machineCache[id] = result;

            return result;
        }

        public void ClearCache()
        {
            _machineCache.Clear();
        }

        // 1. UPDATE QUERY SELECT (Agar saat Admin dibuka, remark lama muncul)
        public List<MachineDetailViewModel> GetAllMachines()
        {
            var list = new List<MachineDetailViewModel>();
            using (var conn = _db.GetConnection())
            {
                try 
                {
                    conn.Open();
                    string query = @"
                        SELECT 
                            l.id, 
                            l.line_production, 
                            l.name, 
                            l.process, 
                            l.remark,  -- JANGAN LUPA INI
                            dr.NilaiA0, 
                            dr.last_update
                        FROM line l
                        LEFT JOIN data_realtime dr ON l.id = dr.id
                        ORDER BY l.line_production ASC, l.id ASC"; // Urutkan berdasarkan ID menaik

                    using (var cmd = new SqlCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var machine = new MachineDetailViewModel
                            {
                                Id = Convert.ToInt32(reader["id"]),
                                Line = reader["line_production"].ToString(),
                                Name = reader["name"].ToString(),
                                Process = reader["process"].ToString(),
                                Remark = reader["remark"] != DBNull.Value ? reader["remark"].ToString() : "", // Mapping
                                NilaiA0 = reader["NilaiA0"] != DBNull.Value ? Convert.ToInt32(reader["NilaiA0"]) : 0
                            };
                            
                            if (reader["last_update"] != DBNull.Value)
                                machine.LastUpdate = Convert.ToDateTime(reader["last_update"]).ToString("yyyy-MM-dd HH:mm:ss");
                            else
                                machine.LastUpdate = "-";

                            list.Add(machine);
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
            return list;
        }

        // 2. UPDATE QUERY UPDATE (Agar tombol Save berfungsi)
        public bool UpdateMachine(int id, string newName, string newProcess, string newLine, string newRemark)
        {
            using (var conn = _db.GetConnection())
            {
                try
                {
                    conn.Open();
                    string query = @"
                        UPDATE line 
                        SET name = @name, 
                            process = @process, 
                            line_production = @line,
                            remark = @remark
                        WHERE id = @id";

                    using (var cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@name", newName);
                        cmd.Parameters.AddWithValue("@process", newProcess);
                        cmd.Parameters.AddWithValue("@line", newLine);
                        // Handle null remark agar tidak error
                        cmd.Parameters.AddWithValue("@remark", (object)newRemark ?? DBNull.Value);

                        int rows = cmd.ExecuteNonQuery();
                        return rows > 0;
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Update Error: {ex.Message}");
                    return false;
                }
            }
        }

        public bool AddMachine(string name, string process, string line, string remark)
            {
                using (var conn = _db.GetConnection())
                {
                    try
                    {
                        conn.Open();
                        // Insert hanya ke tabel 'line'. Data realtime akan otomatis masuk nanti saat ada serial data.
                        // 'line1' kita isi default '-' atau sesuaikan kebutuhan
                        string query = @"
                            INSERT INTO line (line, line_production, process, name, remark) 
                            VALUES ('-', @line, @process, @name, @remark)";

                        using (var cmd = new SqlCommand(query, conn))
                        {
                            cmd.Parameters.AddWithValue("@name", name);
                            cmd.Parameters.AddWithValue("@process", process);
                            cmd.Parameters.AddWithValue("@line", line);
                            cmd.Parameters.AddWithValue("@remark", (object)remark ?? DBNull.Value);

                            int rows = cmd.ExecuteNonQuery();
                            return rows > 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"Add Error: {ex.Message}");
                        return false;
                    }
                }
            }

            public bool DeleteMachine(int id)
            {
                using (var conn = _db.GetConnection())
                {
                    try
                    {
                        conn.Open();
                        
                        // Kita gunakan Transaksi agar jika satu gagal, semua batal (Safety)
                        using (var transaction = conn.BeginTransaction())
                        {
                            try
                            {
                                // 1. Hapus data realtime & shift terkait dulu (opsional, tergantung setting database)
                                // Jika Database Anda sudah set "ON DELETE CASCADE", query ini tidak perlu.
                                // Tapi untuk aman, kita tulis manual:
                                
                                using (var cmd = new SqlCommand("DELETE FROM data_realtime WHERE id = @id", conn, transaction))
                                {
                                    cmd.Parameters.AddWithValue("@id", id);
                                    cmd.ExecuteNonQuery();
                                }

                                // Hapus data di tabel shift (shift_1, shift_2, shift_3)
                                string[] shifts = { "shift_1", "shift_2", "shift_3" };
                                foreach (var table in shifts)
                                {
                                    using (var cmd = new SqlCommand($"DELETE FROM {table} WHERE id = @id", conn, transaction))
                                    {
                                        cmd.Parameters.AddWithValue("@id", id);
                                        cmd.ExecuteNonQuery();
                                    }
                                }

                                // 2. Akhirnya, Hapus Mesin dari tabel induk (Line)
                                using (var cmd = new SqlCommand("DELETE FROM line WHERE id = @id", conn, transaction))
                                {
                                    cmd.Parameters.AddWithValue("@id", id);
                                    int rows = cmd.ExecuteNonQuery();
                                    
                                    // Jika berhasil, Commit transaksi
                                    if (rows > 0)
                                    {
                                        transaction.Commit();
                                        return true;
                                    }
                                    else
                                    {
                                        transaction.Rollback();
                                        return false;
                                    }
                                }
                            }
                            catch
                            {
                                transaction.Rollback(); // Batalkan jika ada error di tengah jalan
                                throw;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Delete Error: {ex.Message}");
                        return false;
                    }
                }
            }
    }
}
