using NeuMo.NeuMoDatabase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Web;
using System.Web.Mvc;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Configuration;
using System.Net.Mail;
using System.Net;
using System.Web.Security;

namespace NeuMo.Controllers
{
    
    public class UnPackingController : Controller
    {
        NeuMoEntities db = new NeuMoEntities();
        // GET: UnPacking
        [Authorize]
        public ActionResult UnPackingAssign()
        {
            //string userName = Session["UserName"].ToString();
            //string userCompany = Session["CompanyName"].ToString();

            //string userName = "sajid";
            //string userCompany = "Emporon";

            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            string initials;

            if (userName.Contains(" "))
            {
                // Split and take first character of each part
                var parts = userName.Split(' ', (char)StringSplitOptions.RemoveEmptyEntries);
                initials = string.Concat(parts[0][0], parts[1][0]).ToUpper();
            }
            else
            {
                // Only first character
                initials = userName[0].ToString().ToUpper();
            }

            ViewBag.UserName = initials;

            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();
            ViewBag.IsAdmin = getUserData.IsAdmin;
            ViewBag.IsUnpacker = getUserData.IsUnpacker;
            ViewBag.IsMechanic = getUserData.IsMechanic;
            ViewBag.BranchName = getUserData.BranchName;

            // Step 1: Get latest placements (in memory)
            var latestPlacements = db.tbl_BikePlacements
                .Where(p => p.IsOccupied && p.Location == getUserData.BranchName)
                .GroupBy(p => p.BoxName)
                .Select(g => g.OrderByDescending(p => p.DateTime).FirstOrDefault())
                .ToList();  // Now it's in memory

            // Step 2: Load all storage slots
            var allSlots = db.tbl_StorageGrid.OrderBy(t => t.ID)
                .ToList();  // Also bring to memory

            // Step 3: Combine them into view model in memory
            var slots = allSlots
                .Select(s =>
                {
                    var placement = latestPlacements.FirstOrDefault(p => p.BoxName == s.BoxName);
                    return new StorageSlotViewModel
                    {
                        BoxName = s.BoxName,
                        IsOccupied = placement != null,
                        IsPriority = placement?.IsPriority ?? false
                    };
                })
                .ToList();

            return View(slots);
        }

        [HttpPost]
        public JsonResult ScanJson(string SerialNo)
        {
            //string userName = Session["UserName"].ToString();
            //string userCompany = Session["CompanyName"].ToString();

            //string userName = "sajid";
            //string userCompany = "Emporon";
            if (!User.Identity.IsAuthenticated)
            {
                // cookie expired → force re-login
                return Json(new { success = false, message = "Die Sitzung ist abgelaufen. Bitte melden Sie sich erneut an." });
            }

            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;


            var odooService = new OdooService();
            var getBarcodeId = odooService.GetStockLotId(SerialNo);

            if (string.IsNullOrEmpty(getBarcodeId))
            {
                return Json(new { success = false, message = "Fahrraddaten sind in der API nicht vorhanden." });
            }



            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();

            var isAlreadyScanned = db.tbl_BikePlacements.Where(u => u.BikeID == SerialNo.Trim() && u.Status != "unmontiert" && u.Status != "abbrechen" && u.Status != "Assigned").FirstOrDefault();
            if(isAlreadyScanned != null)
            {
                return Json(new { success = false, message = "Dieses Fahrrad wurde bereits gescannt. Bitte prüfe das Fahrradpaß-Log." });
            }
            string imageUrl = null;
            var getBikeData = db.vw_NeumoProductDetails.Where(u => u.Bar_Code == getBarcodeId && u.Company == userCompany).FirstOrDefault();
            if(getBikeData != null)
            {
                using (var client = new HttpClient())
                {
                    var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"https://www.fahrrad-xxl.de/json.php?service=fxxlGetProductImageAndData&product_nr={getBikeData.ERFA_No_.Trim()}"
                    );

                    request.Headers.Add("Authorization", "Basic Znh4bC1iaWxkZXJkYXRlbmJhbms6Y2lVT3ZEdTNEMXpqMmk=");
                    request.Headers.Add("Cookie", "PHPSESSID=d3k8u52boeuqrbkr7tiu8on0m5");

                    try
                    {
                        var response = client.SendAsync(request).Result;
                        var content = response.Content.ReadAsStringAsync().Result;
                        dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject(content);
                        imageUrl = json?.image_url;

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error occurred:");
                        Console.WriteLine(ex.Message);
                        return Json(new
                        {
                            success = false,
                            message = "Ein Fehler ist aufgetreten. API-ANFRAGE FEHLGESCHLAGEN."
                            // Optionally add: code = "API_REQUEST_FAILED"
                        });
                    }
                }
            }
            else
            {
                // Return a clear error message with success = false
                return Json(new { success = false, message = "Ungültiger Barcode. Bitte erneut scannen." });
            }
            


            return Json(new
            {
                success = true,
                data = getBikeData,
                image = imageUrl
            });
        }
        [HttpPost]
        public JsonResult AssignBikeSlot(string SerialNo, string barcode, bool isPriority, bool isEbike, string rahmenNumber)
        {

            string preferredRow = "";
            if (isEbike == true)
            {
                preferredRow = "B";
            }
            else
            {
                preferredRow = "A";
            }

            //string userName = Session["UserName"].ToString();
            //string userCompany = Session["CompanyName"].ToString();

            //string userName = "sajid";
            //string userCompany = "Emporon";

            if (!User.Identity.IsAuthenticated)
            {
                // cookie expired → force re-login
                return Json(new { success = false, message = "Die Sitzung ist abgelaufen. Bitte melden Sie sich erneut an." });
            }

            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();

            var odooService = new OdooService();
            // 1️⃣ Call first API → get lot ID
            var lotId = odooService.GetLotId(SerialNo);

            if (string.IsNullOrEmpty(lotId))
            {
                return Json(new { success = false, message = "No lot found for the provided bike ID." });
            }

            // 2️⃣ Call second API → update lot status and frame number
            var updated = odooService.UpdateLotStatus(lotId);

            if (!updated)
            {
                return Json(new { success = false, message = "Failed to update lot status." });
            }


            using (var transaction = db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable))
            {
                try
                {
                    var usedSlots = db.tbl_BikePlacements
                        .Where(p => p.IsOccupied && p.Location == getUserData.BranchName)
                        .Select(p => p.BoxName)
                        .ToList();

                    var availableSlot = db.tbl_StorageGrid
                        .Where(s => s.BoxName.StartsWith(preferredRow) && !usedSlots.Contains(s.BoxName))
                        .OrderBy(s => Guid.NewGuid())
                        .FirstOrDefault();

                    if (availableSlot == null)
                        return Json(new { success = false, message = "Kein verfügbarer Stellplatz gefunden. Bitte Lager prüfen." });

                    // Double-check slot wasn't taken during this transaction
                    bool isSlotTaken = db.tbl_BikePlacements
                        .Any(p => p.BoxName == availableSlot.BoxName && p.IsOccupied);

                    if (isSlotTaken)
                    {
                        transaction.Rollback();
                        return Json(new { success = false, message = "Stellplatz nicht verfügbar. Bitte erneut versuchen." });
                    }

                    db.tbl_BikePlacements.Add(new tbl_BikePlacements
                    {
                        BoxName = availableSlot.BoxName,
                        IsOccupied = true,
                        IsPriority = isPriority,
                        Location = getUserData.BranchName,
                        DateTime = DateTime.Now,
                        BikeID = SerialNo.Trim(),
                        FrameNumber = rahmenNumber,
                        UnPackerID = getUserData.UserId,
                        IsEbike= isEbike,
                        CompanyName= userCompany,
                        Barcode = barcode,
                    });

                    db.SaveChanges();
                    transaction.Commit();

                    return Json(new { success = true, assignedBox = availableSlot.BoxName });
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return Json(new { success = false, message = "Fehler bei der Zuweisung des Stellplatzes." });
                }
            }
        }

        [HttpPost]
        public JsonResult UnpackingWithFrameNumber(string SerialNo, string barcode, bool isPriority, bool isEbike, string rahmenNumber)
        {


            //string userName = Session["UserName"].ToString();
            //string userCompany = Session["CompanyName"].ToString();

            //string userName = "sajid";
            //string userCompany = "Emporon";

            if (!User.Identity.IsAuthenticated)
            {
                // cookie expired → force re-login
                return Json(new { success = false, message = "Die Sitzung ist abgelaufen. Bitte melden Sie sich erneut an." });
            }

            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            if (rahmenNumber != "" || rahmenNumber == "")
            {
                var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();
                if (getUserData == null)
                    return Json(new { success = false, message = "Benutzerdaten existieren nicht." });

                var odooService = new OdooService();
                // 1️⃣ Call first API → get lot ID
                var lotId = odooService.GetLotId(SerialNo);

                if (string.IsNullOrEmpty(lotId))
                {
                    return Json(new { success = false, message = "No lot found for the provided bike ID." });
                }

                // 2️⃣ Call second API → update lot status and frame number
                var updated = odooService.UpdateLotStatus(lotId);

                if (!updated)
                {
                    return Json(new { success = false, message = "Failed to update lot status." });
                }


                string imageUrl = null;
                var getBikeData = db.vw_NeumoProductDetails.Where(u => u.Company == userCompany && u.Bar_Code == barcode).FirstOrDefault();
                if (getBikeData == null)
                    return Json(new { success = false, message = "Fahrraddaten sind nicht vorhanden." });
                if (getBikeData != null)
                {
                    db.tbl_BikePlacements.Add(new tbl_BikePlacements
                    {
                        BoxName = "",
                        Status = "UnPackingWithFrameNumber",
                        IsOccupied = false,
                        IsPriority = false,
                        Location = getUserData.BranchName,
                        DateTime = DateTime.Now,
                        BikeID = SerialNo.Trim(),
                        //AssemblerID = getUserData.UserId,
                        FrameNumber = rahmenNumber,
                        UnPackerID = getUserData.UserId,
                        IsEbike = isEbike,
                        CompanyName = userCompany,
                        Barcode= barcode
                    });

                    // Log print info
                    var bikeLog = new tbl_BikePassPrint_Log
                    {
                        Date = DateTime.Now,
                        SerialNumber = SerialNo,
                        Frame_Num = rahmenNumber,
                        BranchName = getUserData.BranchName,
                        Monteur = userName,
                        AccesoryBag = "--",
                        BatteryNumber = "--",
                        KeyNumber = "--",
                        Barcode= getBikeData.Bar_Code,
                        CompanyName = userCompany
                    };

                    db.tbl_BikePassPrint_Log.Add(bikeLog);

                    // ✅ Only call SaveChanges once
                    int rows = db.SaveChanges();
                    if (rows > 0)
                    {
                        return Json(new { success = true, message = "Daten erfolgreich gespeichert." });
                    }
                    else
                    {
                        return Json(new { success = false, message = "Daten konnten nicht gespeichert werden." });
                    }



                }
            }

            return Json(new { success = true });
        }


        [HttpPost]
        public ActionResult Melden(string bikeId)
        {
            string userName = Session["UserName"].ToString();
            string userCompany = Session["CompanyName"].ToString();

            //string userName = "sajid";
            //string userCompany = "Emporon";

            var getBikeData = db.vw_NeumoProductDetails.FirstOrDefault(u => u.Bar_Code == bikeId && u.Company == userCompany);
            if (getBikeData == null)
            {
                return Json(new { success = false, message = "Produkt nicht gefunden" });
            }

            var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
            if (getUserData == null)
            {
                return Json(new { success = false, message = "Benutzer nicht gefunden" });
            }


        //    string subject = $"Neuer Mangel gemeldet";

        //    string body = $@"
        //<p style='font-family:Segoe UI, sans-serif; font-size:16px;'>
        //  Hallo,<br><br>
        //  für dieses Rad hat die Seriennummer nicht mit dem Rad übereingestimmt:<br><br>

        // Artikelnummer: {getBikeData.cr023_artikelid} <br>
        // Seriennummer: {bikeId}<br>
        // Produkt-Information: {getBikeData.cr023_marke}; {getBikeData.cr023_beschreibung}<br>
        // Unpacker: {getUserData.UserName}<br><br>

         
        // mit freundlichen Grüßen!
        //</p>";

        //    bool isProduction = Convert.ToBoolean(ConfigurationManager.AppSettings["IsProduction"]);
        //    string location = getUserData.BranchName?.ToUpper();
        //    var GetRecieverData = db.tbl_Branches.FirstOrDefault(u => u.BranchName.ToUpper() == location);

        //    try
        //    {
        //        using (MailMessage mail = new MailMessage())
        //        {
        //            mail.From = new MailAddress("scanner@fahrrad-xxl.de");
        //            if (!isProduction)
        //                mail.To.Add("odoo@fahrrad-xxl.de");
        //            else
        //                mail.To.Add(GetRecieverData?.Email ?? "odoo@fahrrad-xxl.de");

        //            mail.Bcc.Add("odoo@fahrrad-xxl.de");
        //            mail.Subject = subject;
        //            mail.Body = body;
        //            mail.IsBodyHtml = true;

        //            using (SmtpClient client = new SmtpClient("192.168.94.254", 25))
        //            {
        //                client.Credentials = new NetworkCredential("scanner@fahrrad-xxl.de", "jkJyOpPAoN16ohIU");
        //                client.EnableSsl = false;
        //                client.Send(mail);
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        return Json(new { success = false, message = "Fehler beim Senden der E-Mail: " + ex.Message });
        //    }


            return Json(new { success = true });
        }

        public class StorageSlotViewModel
        {
            public string BoxName { get; set; }
            public bool IsOccupied { get; set; }
            public bool IsPriority { get; set; }
            public string RowGroup => BoxName.Substring(0, 1);
        }

    }
}