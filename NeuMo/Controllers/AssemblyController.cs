using NeuMo.NeuMoDatabase;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using iTextSharp.text;
using iTextSharp.text.pdf;
using Image = iTextSharp.text.Image;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using static NeuMo.Controllers.UnPackingController;
using System.Xml.Linq;
using System.Data.Entity;
using System.Web.Security;
using System.Web.Helpers;
using System.Text;
using Newtonsoft.Json.Linq;
using ClosedXML.Excel;
using System.Data;
using PagedList;
using PagedList.Mvc;
using System.Threading.Tasks;
//using Microsoft.AspNetCore.Mvc;

namespace NeuMo.Controllers
{
    public class AssemblyController : Controller
    {
        NeuMoEntities db = new NeuMoEntities();

       
        //
        // GET: Assembly
        [Authorize]
        public ActionResult FahrradMontage()
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

            ViewBag.UserNameAndCompany = userName + userCompany;


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
        public JsonResult AssignNextMontageJob()
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

            string imageUrl = "";

            using (var transaction = db.Database.BeginTransaction())
            {
                var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);

                if (getUserData == null)
                {
                    return Json(new { success = false, message = "Benutzerdaten nicht gefunden." });
                }

                // Get candidate jobs
                var candidateJobs = db.tbl_BikePlacements
                    .Where(p => p.IsOccupied && p.Location == getUserData.BranchName && p.Status != "Assigned")
                    .GroupBy(p => p.BoxName)
                    .Select(g => g.OrderByDescending(p => p.DateTime).FirstOrDefault())
                    .OrderByDescending(p => p.IsPriority)
                    .ThenBy(p => p.DateTime)
                    .ToList();

                tbl_BikePlacements nextJob = null;

                foreach (var job in candidateJobs)
                {
                    // Try to lock this specific job if still available
                    var lockedJob = db.tbl_BikePlacements
                        .FirstOrDefault(p => p.ID == job.ID && p.IsOccupied == true);

                    if (lockedJob != null)
                    {
                        nextJob = lockedJob;
                        nextJob.Status = "Assigned";
                        nextJob.AssemblerID = getUserData.UserId;
                        db.SaveChanges();

                        transaction.Commit();
                        break;
                    }
                }

                if (nextJob == null)
                {
                    return Json(new { success = false, message = "Kein Job verfügbar oder wurde bereits vergeben." });
                }


                var getBikeData = db.vw_NeumoProductDetails
                    .FirstOrDefault(u => u.Bar_Code == nextJob.Barcode && u.Company == userCompany);


                if (getBikeData != null)
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
                            Console.WriteLine("API-Fehler: " + ex.Message);
                            return Json(new { success = false, message = "Fehler beim Abrufen von Bilddaten." });
                        }
                    }
                }
                else
                {
                    return Json(new { success = false, message = "Fahrraddaten existieren nicht." });
                }

                var mechanicList = db.Users.Where(U => U.BranchName == getUserData.BranchName)
                                   .Select(u => new { u.UserId, u.UserName }) // include only necessary fields
                                   .ToList();
                bool IsRCategory = !string.IsNullOrEmpty(getBikeData?.No_) &&
                                   getBikeData.No_[0].ToString().Equals("R", StringComparison.OrdinalIgnoreCase);

                var bikeData = new
                {
                    success = true,
                    boxName = nextJob.BoxName,
                    bikeId = nextJob.BikeID,
                    FrameNr = nextJob.FrameNumber,
                    imageUrl = imageUrl,
                    data = getBikeData,
                    mechanics = mechanicList,
                    isRCategory = IsRCategory,
                    currentUserName = getUserData.UserName,
                };

                return Json(bikeData);
            }
        }

        [HttpPost]
        public JsonResult getDataForReprint(int id, string frameNo)
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

            string imageUrl = "";

            var getBikePassData = db.tbl_BikePassPrint_Log.Where(u => u.Id == id && u.Frame_Num == frameNo).FirstOrDefault();
            if (getBikePassData == null)
            {
                return Json(new { success = false, message = "Fahrraddaten existieren nicht." });
            }

            Session.Remove("getBikeData");
            var getBikeData = db.vw_NeumoProductDetails
                   .FirstOrDefault(u => u.Bar_Code == getBikePassData.Barcode && u.Company == userCompany);
            Session["getBikeData"] = JsonConvert.SerializeObject(getBikeData);

            var assembleBikeData = db.tbl_BikePlacements
                   .FirstOrDefault(u => u.BikeID == getBikePassData.SerialNumber && u.Status == "montiert");
            //if (assembleBikeData == null)
            //{
            //    return Json(new { success = false, message = "Fahrraddaten existieren nicht." });
            //}
            if (assembleBikeData == null)
            {
                assembleBikeData = new tbl_BikePlacements
                {
                    BikeID = getBikePassData.SerialNumber,
                    Status = "Logix",
                    AssembleEndTime = DateTime.Now,
                    AssembleStartTime = DateTime.Now,
                    DateTime = DateTime.Now

                    // Set other required fields if any
                };
            }


            var getUserData = db.Users.Where(u => u.UserId == assembleBikeData.UnPackerID).FirstOrDefault();

            if (getBikeData != null)
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
                        Console.WriteLine("API-Fehler: " + ex.Message);
                        return Json(new { success = false, message = "Fehler beim Abrufen von Bilddaten." });
                    }
                }
            }
            else
            {
                return Json(new { success = false, message = "Fahrraddaten existieren nicht." });
            }
            List<SerializedProduct> products = new List<SerializedProduct>();

            string connectionString = ConfigurationManager.ConnectionStrings["EmporonDBConnection"].ConnectionString;
            string navTableName = ConfigurationManager.AppSettings["SerializedProductsTable"];
            string selectQuery = $@"
                                SELECT 
                                serial_num, 
                                [Item No_], 
                                frame_num, 
                                battery_num, 
                                accessory_bag, 
                                fork_num, 
                                created_at
                                 FROM {navTableName}
                                 WHERE serial_num = @serial_num";

            using (SqlConnection connection = new SqlConnection(connectionString))
            using (SqlCommand command = new SqlCommand(selectQuery, connection))
            {
                command.Parameters.AddWithValue("@serial_num", getBikePassData.SerialNumber);

                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        products.Add(new SerializedProduct
                        {
                            SerialNum = reader["serial_num"].ToString(),
                            ItemNo = reader["Item No_"].ToString(),
                            FrameNum = reader["frame_num"].ToString(),
                            BatteryNum = reader["battery_num"].ToString(),
                            AccessoryBag = reader["accessory_bag"].ToString(),
                            ForkNum = reader["fork_num"].ToString(),
                            CreatedAt = Convert.ToDateTime(reader["created_at"])
                        });
                    }
                }
            }
            if (products.Count == 0)
            {
                products.Add(new SerializedProduct
                {
                    SerialNum = getBikePassData.SerialNumber,
                    FrameNum = getBikePassData.Frame_Num,
                    BatteryNum = getBikePassData.BatteryNumber,
                    AccessoryBag = getBikePassData.AccesoryBag,
                    ForkNum = getBikePassData.ForkNumber,
                    CreatedAt = getBikePassData.Date ?? DateTime.Now
                });
                // No data found
                //return Json(new { success = false, message = "Fahrraddaten existieren nicht." }, JsonRequestBehavior.AllowGet);
            }

            var getEmployeeNumber = db.Users.Where(u => u.UserName == getBikePassData.Mechaniker).FirstOrDefault();

            var bikeData = new
            {
                success = true,
                //boxName = nextJob.BoxName,
                bikeId = getBikePassData.SerialNumber,
                imageUrl = imageUrl,
                data = getBikeData,
                navisionData = products,
                assembleBikeData = assembleBikeData,
                assemblerName = getBikePassData.Mechaniker,
                unpackerName = getUserData?.UserName ?? "",
                employeeNumber = getEmployeeNumber?.EmployeeNumber ?? "",


            };


            return Json(bikeData);
        }

        [HttpPost]
        public JsonResult FreeUpSpace(string bikeId)
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

            var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
            var updateBikeData = db.tbl_BikePlacements.Where(u => u.BikeID == bikeId && u.Status == "Assigned" && u.Location == getUserData.BranchName).OrderByDescending(u => u.DateTime).FirstOrDefault();
            if (updateBikeData == null)
            {
                return Json(new { success = false, message = "Fahrrad nicht gefunden oder bereits montiert." });
            }

            updateBikeData.IsOccupied = false;
            db.SaveChanges();




            return Json(new { success = true, message = "Success" });


        }

        [HttpPost]
        public JsonResult ScanBike(string barcode, string bikeId)
        {


            if (barcode.Trim() != bikeId.Trim())
            {
                // Return a clear error message with success = false
                return Json(new { success = false, message = "Ungültiger Barcode. Bitte erneut scannen." });
            }
            else
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



                var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);

                var getBikeData = db.tbl_BikePlacements.Where(u => u.BikeID == bikeId && u.Status == "Assigned" && u.Location == getUserData.BranchName).OrderByDescending(u=> u.DateTime).FirstOrDefault();
                getBikeData.AssembleStartTime = DateTime.Now;
                db.SaveChanges();
            }

            return Json(new { success = true, message = "Success" });
        }
        [HttpPost]
        public JsonResult ValidateRahmennummer(string FrameData, string bikeBrand)
        {

            bool isMatch = false;
            var getBrandPatternData = db.tbl_FrameNumberPattern.Where(u => u.BrandDesignation == bikeBrand).ToList();

            foreach (var item in getBrandPatternData)
            {
                var pattern = item.Pattern;
                if (!string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrEmpty(FrameData))
                {
                    isMatch = Regex.IsMatch(FrameData, pattern, RegexOptions.IgnoreCase);
                    if (isMatch)
                        break;
                }
            }
            if (!isMatch)
            {
                
                return Json(new { success = false, message = "Falsche Framenummer." });
            }

            return Json(new { success = true, message = "Success" });
        }

        [HttpPost]
        public JsonResult NotifyAdminWrongFrameNo(string FrameData, string bikeBrand, string bikeId)
        {
            // 1. Validate user session
            if (!User.Identity.IsAuthenticated)
            {
                return Json(new { success = false, message = "Die Sitzung ist abgelaufen. Bitte melden Sie sich erneut an." });
            }

            // 2. Validate request fields
            if (string.IsNullOrWhiteSpace(FrameData))
                return Json(new { success = false, message = "Rahmennummer fehlt." });

            if (string.IsNullOrWhiteSpace(bikeBrand))
                return Json(new { success = false, message = "Marke fehlt." });

            if (string.IsNullOrWhiteSpace(bikeId))
                return Json(new { success = false, message = "Fahrrad-ID fehlt." });

            // 3. Get user data
            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);

            var getBikeBarcode = db.tbl_BikePlacements.FirstOrDefault(u => u.BikeID == bikeId );

            if (getUserData == null)
                return Json(new { success = false, message = "Benutzerdaten nicht gefunden." });

            // 4. Email subject & body
            string subject = $"Falsch Rahmennummernmuster";

            string body = $@"
    <p style='font-family:Segoe UI, sans-serif; font-size:16px;'>
        Hallo Sabine,<br><br>
        von dieser Seriennummer [{bikeId}] hat sich die Rahmennummer-Logik geändert.<br><br>
        Die Rahmennummer ist: [{FrameData}]<br><br>

        Barcode: [{getBikeBarcode.Barcode}]<br>
        Filiale: [{getUserData.BranchName}]<br>
        Marke: [{bikeBrand}]<br>
        Name: [{userName}]<br>
        Datum: [{DateTime.Now}]<br><br>
        Danke
    </p>";

            bool isProduction = Convert.ToBoolean(ConfigurationManager.AppSettings["IsProduction"]);


            // 5. Prevent duplicate emails within the same session
            string sessionKey = "FrmNotify_" + bikeId + "_" + FrameData;

            if (Session[sessionKey] != null)
            {
                return Json(new { success = false, message = "Diese Meldung wurde bereits gesendet." });
            }


            // 6. Send email
            try
            {
                using (MailMessage mail = new MailMessage())
                {
                    mail.From = new MailAddress("scanner@fahrrad-xxl.de");

                    if (!isProduction)
                    {
                        //mail.To.Add("s.sharif@fahrrad-xxl.de");
                        mail.To.Add("odoo@fahrrad-xxl.de");
                    }
                    else
                    {
                        mail.To.Add("s.neumann@fahrrad-xxl.de");
                    }

                    mail.Bcc.Add("odoo@fahrrad-xxl.de");
                    mail.Subject = subject;
                    mail.Body = body;
                    mail.IsBodyHtml = true;

                    using (SmtpClient client = new SmtpClient("192.168.94.254", 25))
                    {
                        client.Credentials = new NetworkCredential("scanner@fahrrad-xxl.de", "jkJyOpPAoN16ohIU");
                        client.EnableSsl = false;

                        client.Send(mail); // quick (local SMTP)
                    }
                }

                // Mark as sent
                Session[sessionKey] = true;

                return Json(new { success = true, message = "Admin erfolgreich informiert." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "E-Mail konnte nicht gesendet werden." });
            }
        }



        [HttpPost]
        public ActionResult CompleteJob(FormCollection form)
        {
            try
            {
                // Retrieve user and company from session
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

                if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(userCompany))
                    return Json(new { success = false, message = "Benutzerdaten fehlen." });

                // Extract form values
                string rahmennummer = form["rahmennummer"];
                string schluesselnummer = form["Schlüsselnummer"];
                string akkuNummer = form["Akkunummer"];
                string zubehoerTasche = form["zubehoerTasche"];
                string gabelNummer = form["gabelNummer"];
                string bikeId = form["bikeId"];
                string barcode = form["barcode"];

                string mechanicName = form["mechanicDropdown"];

                string UnPackingWithFrameNumber = form["UnPackingWithFrameNumber"];
                bool isDifficult = bool.TryParse(form["isDifficultCheckbox"], out var result) && result;


                var getBikeData = db.vw_NeumoProductDetails.Where(u => u.Bar_Code == barcode && u.Company == userCompany).FirstOrDefault();
                if (getBikeData == null)
                    return Json(new { success = false, message = "Fehler beim Lesen der Fahrrad-Daten." });

                if (string.IsNullOrWhiteSpace(rahmennummer) &&
                    !string.IsNullOrEmpty(getBikeData.No_) &&
                    getBikeData.No_[0].ToString().Equals("R", StringComparison.OrdinalIgnoreCase))
                {
                    rahmennummer = bikeId; // assign bikeId to rahmennummer
                }


                // Retrieve user info from your local DB
                var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
                if (getUserData == null)
                    return Json(new { success = false, message = "Benutzerinformationen nicht gefunden." });



                // Call first api 

                var apiUrl = ConfigurationManager.AppSettings["OdooApiUrl"];
                var dbName = ConfigurationManager.AppSettings["OdooDatabase"];
                var userId = int.Parse(ConfigurationManager.AppSettings["OdooUserId"]);
                var apiKey = ConfigurationManager.AppSettings["OdooApiKey"];

                var getId = "";
                var client = new HttpClient();

                var requestBody = new
                {
                    jsonrpc = "2.0",
                    method = "call",
                    @params = new
                    {
                        service = "object",
                        method = "execute_kw",
                        args = new object[]
                        {
                           dbName, // From config
                           userId, // From config
                           apiKey, // From config
                            "stock.lot",
                            "search_read",
                           new object[]
                            {
                              new object[]
                                  {
                                     new object[] {"name", "=", bikeId}
                                  },
                              new string[] {"id","name","product_id","status","frame_number"}
                              },
                            new { }
                        }
                    }
                };

                var content = new StringContent(
                    JsonConvert.SerializeObject(requestBody),
                    Encoding.UTF8,
                    "application/json"
                );

                try
                {
                    var response = client.PostAsync(apiUrl, content).Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        return Json(new { success = false, message = $"HTTP Error: {(int)response.StatusCode} - {response.ReasonPhrase}" });
                    }

                    var responseString = response.Content.ReadAsStringAsync().Result;
                    var rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(responseString);

                    if (rpcResponse.Error != null && rpcResponse.Error.HasValues)
                    {
                        return Json(new { success = false, message = $"RPC Error: {rpcResponse.Error}" });
                    }

                    // response is successful → extract the id
                    if (rpcResponse.Result != null)
                    {
                        // Assuming Result is a JArray of objects
                        var resultArray = rpcResponse.Result as JArray;
                        if (resultArray != null && resultArray.Count > 0)
                        {
                            var firstItem = resultArray[0];
                            getId = firstItem["id"]?.ToString();


                        }
                    }


                }
                catch (Exception ex)
                {
                    return Json(new { success = false, message = $"Exception: {ex.Message}" });
                }



                // Update Frame number and  status
                var bikeStatusInOdoo = "";

                if(getUserData.BranchName == "DDN" || getUserData.BranchName == "DDW" || getUserData.BranchName == "CHE" || getUserData.BranchName == "LEI" || getUserData.BranchName == "HAL")
                {
                    bikeStatusInOdoo = "assembled";
                }
                else
                {
                    bikeStatusInOdoo = "unpacked";
                }

                try
                {
                    var clientTwo = new HttpClient();
                    var requestTwo = new HttpRequestMessage(HttpMethod.Post, apiUrl);

                    var contentTwo = new StringContent(
                                     $@"{{
                                           ""jsonrpc"": ""2.0"",
                                           ""method"": ""call"",
                                           ""params"": {{
                                            ""service"": ""object"",
                                            ""method"": ""execute_kw"",
                                             ""args"": [
                                               ""{dbName}"",
                                             {userId},
                                            ""{apiKey}"",
                                                 ""stock.lot"",
                                            ""write"",
                                            [
                                              [{getId}],
                                              {{
                                             ""status"": ""{bikeStatusInOdoo}"",
                                              ""frame_number"": ""{rahmennummer}""
                                                   }}
                                            ]
                                         ]
                                            }},
                                              ""id"": 2
                                             }}",
                                          Encoding.UTF8,
                                       "application/json"
                    );

                    requestTwo.Content = contentTwo;

                    var response = client.SendAsync(requestTwo).Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        return Json(new { success = false, message = $"HTTP Error: {(int)response.StatusCode} - {response.ReasonPhrase}" });
                    }

                    var responseString = response.Content.ReadAsStringAsync().Result;
                    var rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(responseString);

                    if (rpcResponse.Error != null && rpcResponse.Error.HasValues)
                    {
                        return Json(new { success = false, message = $"RPC Error: {rpcResponse.Error}" });
                    }

                    // "result": true → update succeeded
                    if (rpcResponse.Result != null && rpcResponse.Result.ToString().ToLower() == "true")
                    {

                    }


                }
                catch (Exception ex)
                {
                    return Json(new { success = false, message = $"Exception: {ex.Message}" });
                }














                if (UnPackingWithFrameNumber == "1")
                {
                    var updateBikeData = db.tbl_BikePlacements
                       .FirstOrDefault(u => u.BikeID == bikeId && u.CompanyName == userCompany && u.AssemblerID == getUserData.UserId && u.Status == "UnPackingWithFrameNumber" );

                    if (updateBikeData != null)
                    {
                        updateBikeData.AssembleEndTime = DateTime.Now;
                        updateBikeData.Status = "montiert";
                        updateBikeData.Location = getUserData.BranchName;
                        updateBikeData.AssemblerID = getUserData.UserId;
                        updateBikeData.IsDifficult = false;
                        updateBikeData.KeyNumber = schluesselnummer;
                        updateBikeData.Mechaniker = mechanicName;
                    }

                    var getBikePassData = db.tbl_BikePassPrint_Log.Where(u => u.SerialNumber == bikeId && u.CompanyName == userCompany  && u.AccesoryBag == "--" && u.BatteryNumber == "--" && u.KeyNumber == "--").FirstOrDefault();
                    if (getBikePassData != null)
                    {
                        getBikePassData.BranchName = getUserData.BranchName;
                        getBikePassData.Monteur = userName;
                        getBikePassData.AccesoryBag = zubehoerTasche;
                        getBikePassData.BatteryNumber = akkuNummer;
                        getBikePassData.KeyNumber = schluesselnummer;
                        getBikePassData.Mechaniker = mechanicName;
                        getBikePassData.Frame_Num = rahmennummer;
                        getBikePassData.Brand = getBikeData.Brand;
                        getBikePassData.Model = getBikeData.Description;

                    }
                    db.SaveChanges();
                    try
                    {
                        string connectionString = ConfigurationManager.ConnectionStrings["EmporonDBConnection"].ConnectionString;

                        string navTableName = ConfigurationManager.AppSettings["SerializedProductsTable"];
                        string navReplTableName = ConfigurationManager.AppSettings["SerializedProductsReplTable"];

                        string upsertQuery = $@"
                IF EXISTS (SELECT 1 FROM {navTableName} WHERE serial_num = @serial_num)
                BEGIN
                    UPDATE {navTableName}
                    SET [Item No_] = @item_no,
                        frame_num = @frame_num,
                        battery_num = @battery_num,
                        accessory_bag = @accessory_bag,
                        fork_num = @fork_num,
                        created_at = @created_at
                    WHERE serial_num = @serial_num;
                END
                ELSE
                BEGIN
                    INSERT INTO {navTableName}
                    (serial_num, [Item No_], frame_num, battery_num, accessory_bag, fork_num, created_at)
                    VALUES (@serial_num, @item_no, @frame_num, @battery_num, @accessory_bag, @fork_num, @created_at);
                END";

                        using (var connection = new SqlConnection(connectionString))
                        using (var command = new SqlCommand(upsertQuery, connection))
                        {
                            command.Parameters.AddWithValue("@serial_num", bikeId);
                            command.Parameters.AddWithValue("@item_no", getBikeData.No_);
                            command.Parameters.AddWithValue("@frame_num", rahmennummer);
                            command.Parameters.AddWithValue("@battery_num", akkuNummer);
                            command.Parameters.AddWithValue("@accessory_bag", zubehoerTasche);
                            command.Parameters.AddWithValue("@fork_num", "");
                            command.Parameters.AddWithValue("@created_at", DateTime.Now.Date);

                            connection.Open();
                            command.ExecuteNonQuery();
                        }

                        string upsertQueryTwo = $@"
                                              IF EXISTS (SELECT 1 FROM {navReplTableName} WHERE serial_num = @serial_num)
                                              BEGIN
                                              UPDATE {navReplTableName}
                                              SET [Item No_] = @item_no,
                                             frame_num = @frame_num,
                                              battery_num = @battery_num,
                                             accessory_bag = @accessory_bag,
                                             fork_num = @fork_num,
                                             created_at = @created_at
                                             WHERE serial_num = @serial_num;
                                             END
                                             ELSE
                                            BEGIN
                                                INSERT INTO {navReplTableName}
                                           (serial_num, [Item No_], frame_num, battery_num, accessory_bag, fork_num, created_at)
                                          VALUES (@serial_num, @item_no, @frame_num, @battery_num, @accessory_bag, @fork_num, @created_at);
                                           END";

                        using (var connection = new SqlConnection(connectionString))
                        using (var commandTwo = new SqlCommand(upsertQueryTwo, connection))
                        {
                            commandTwo.Parameters.AddWithValue("@serial_num", bikeId);
                            commandTwo.Parameters.AddWithValue("@item_no", getBikeData.No_);
                            commandTwo.Parameters.AddWithValue("@frame_num", rahmennummer);
                            commandTwo.Parameters.AddWithValue("@battery_num", akkuNummer);
                            commandTwo.Parameters.AddWithValue("@accessory_bag", zubehoerTasche);
                            commandTwo.Parameters.AddWithValue("@fork_num", "");
                            commandTwo.Parameters.AddWithValue("@created_at", DateTime.Now.Date);

                            connection.Open();
                            commandTwo.ExecuteNonQuery();
                        }
                    }
                    catch (Exception ex)
                    {
                        return Json(new { success = false, message = "Fehler beim Speichern in die Emporon-Datenbank: " + ex.Message });
                    }

                }
                else
                {
                    int bikePlacementId = 0;


                    // Update bike placement info
                    var updateBikeData = db.tbl_BikePlacements.Where(u => u.BikeID == bikeId && u.CompanyName == userCompany &&
                                      u.AssemblerID == getUserData.UserId && u.Status == "Assigned" &&
                                      u.Location == getUserData.BranchName)
                                       .OrderByDescending(u => u.DateTime)   // <-- newest row
                                     .FirstOrDefault();

                    if (updateBikeData != null)
                    {
                        bikePlacementId = updateBikeData.ID;

                        updateBikeData.AssembleEndTime = DateTime.Now;
                        updateBikeData.Status = "montiert";
                        updateBikeData.IsDifficult = isDifficult;
                        updateBikeData.KeyNumber = schluesselnummer;
                        updateBikeData.Mechaniker = mechanicName;
                    }
                    else
                    {
                        var direktMontageBikeData = db.tbl_BikePlacements
                        .Where(u => u.BikeID == bikeId && u.CompanyName == userCompany && u.AssemblerID == getUserData.UserId && u.Status == "DirektMontage" && u.Location == getUserData.BranchName)
                        .OrderByDescending(u => u.DateTime)   // <-- newest row
                                     .FirstOrDefault();

                        if (direktMontageBikeData != null)
                        {
                            bikePlacementId = direktMontageBikeData.ID;

                            direktMontageBikeData.AssembleEndTime = DateTime.Now;
                            direktMontageBikeData.Status = "montiert";
                            direktMontageBikeData.IsDifficult = isDifficult;
                            direktMontageBikeData.KeyNumber = schluesselnummer;
                            direktMontageBikeData.Mechaniker = mechanicName;

                        }

                    }

                    // Save bike data into Emporon DB
                    try
                    {
                        string connectionString = ConfigurationManager.ConnectionStrings["EmporonDBConnection"].ConnectionString;

                        string navTableName = ConfigurationManager.AppSettings["SerializedProductsTable"];
                        string navReplTableName = ConfigurationManager.AppSettings["SerializedProductsReplTable"];

                        string upsertQuery = $@"
                IF EXISTS (SELECT 1 FROM {navTableName} WHERE serial_num = @serial_num)
                BEGIN
                    UPDATE {navTableName}
                    SET [Item No_] = @item_no,
                        frame_num = @frame_num,
                        battery_num = @battery_num,
                        accessory_bag = @accessory_bag,
                        fork_num = @fork_num,
                        created_at = @created_at
                    WHERE serial_num = @serial_num;
                END
                ELSE
                BEGIN
                    INSERT INTO {navTableName}
                    (serial_num, [Item No_], frame_num, battery_num, accessory_bag, fork_num, created_at)
                    VALUES (@serial_num, @item_no, @frame_num, @battery_num, @accessory_bag, @fork_num, @created_at);
                END";

                        using (var connection = new SqlConnection(connectionString))
                        using (var command = new SqlCommand(upsertQuery, connection))
                        {
                            command.Parameters.AddWithValue("@serial_num", bikeId);
                            command.Parameters.AddWithValue("@item_no", getBikeData.No_);
                            command.Parameters.AddWithValue("@frame_num", rahmennummer);
                            command.Parameters.AddWithValue("@battery_num", akkuNummer);
                            command.Parameters.AddWithValue("@accessory_bag", zubehoerTasche);
                            command.Parameters.AddWithValue("@fork_num", "");
                            command.Parameters.AddWithValue("@created_at", DateTime.Now.Date);

                            connection.Open();
                            command.ExecuteNonQuery();
                        }



                        string upsertQueryTwo = $@"
                                              IF EXISTS (SELECT 1 FROM {navReplTableName} WHERE serial_num = @serial_num)
                                              BEGIN
                                              UPDATE {navReplTableName}
                                              SET [Item No_] = @item_no,
                                             frame_num = @frame_num,
                                              battery_num = @battery_num,
                                             accessory_bag = @accessory_bag,
                                             fork_num = @fork_num,
                                             created_at = @created_at
                                             WHERE serial_num = @serial_num;
                                             END
                                             ELSE
                                            BEGIN
                                                INSERT INTO {navReplTableName}
                                           (serial_num, [Item No_], frame_num, battery_num, accessory_bag, fork_num, created_at)
                                          VALUES (@serial_num, @item_no, @frame_num, @battery_num, @accessory_bag, @fork_num, @created_at);
                                           END";

                        using (var connection = new SqlConnection(connectionString))
                        using (var commandTwo = new SqlCommand(upsertQueryTwo, connection))
                        {
                            commandTwo.Parameters.AddWithValue("@serial_num", bikeId);
                            commandTwo.Parameters.AddWithValue("@item_no", getBikeData.No_);
                            commandTwo.Parameters.AddWithValue("@frame_num", rahmennummer);
                            commandTwo.Parameters.AddWithValue("@battery_num", akkuNummer);
                            commandTwo.Parameters.AddWithValue("@accessory_bag", zubehoerTasche);
                            commandTwo.Parameters.AddWithValue("@fork_num", "");
                            commandTwo.Parameters.AddWithValue("@created_at", DateTime.Now.Date);

                            connection.Open();
                            commandTwo.ExecuteNonQuery();
                        }
                    }
                    catch (Exception ex)
                    {
                        return Json(new { success = false, message = "Fehler beim Speichern in die Emporon-Datenbank: " + ex.Message });
                    }



                    // Log print info
                    db.tbl_BikePassPrint_Log.Add(new tbl_BikePassPrint_Log
                    {
                        Date = DateTime.Now,
                        SerialNumber = bikeId,
                        Barcode = getBikeData.Bar_Code,
                        Frame_Num = rahmennummer,
                        BranchName = getUserData.BranchName,
                        Monteur = userName,
                        AccesoryBag = zubehoerTasche,
                        BatteryNumber = akkuNummer,
                        KeyNumber = schluesselnummer,
                        ForkNumber = gabelNummer,
                        CompanyName = userCompany,
                        Mechaniker = mechanicName,
                        Brand = getBikeData.Brand,
                        Model = getBikeData.Description
                    });

                    // Commit DB changes
                    db.SaveChanges();

                    return Json(new { success = true, message = "Fahrrad erfolgreich abgeschlossen." });




                }
                return Json(new { success = true, message = "Fahrrad erfolgreich abgeschlossen." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Unerwarteter Fehler: " + ex.Message });
            }

        }

        [HttpPost]
        public JsonResult UpdateRahmennummer(string rahmennummer, string bikeId, string akku, string key, string zubehor)
        {

            bool isMatch = false;
            var getBikepassData = db.tbl_BikePassPrint_Log.Where(u => u.SerialNumber == bikeId).FirstOrDefault();
            if (getBikepassData == null)
            {
                return Json(new { success = false, message = "Keine Daten für diese Seriennummer gefunden." });
            }

            var bikeData = db.vw_NeumoProductDetails.Where(u => u.Bar_Code == getBikepassData.Barcode).FirstOrDefault();
            if (bikeData == null)
            {
                return Json(new { success = false, message = "Produktdetails nicht gefunden." });
            }

            var getBrandPatternData = db.tbl_FrameNumberPattern.Where(u => u.BrandDesignation == bikeData.Brand).ToList();

            foreach (var item in getBrandPatternData)
            {
                var pattern = item.Pattern;
                if (!string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrEmpty(rahmennummer))
                {
                    isMatch = Regex.IsMatch(rahmennummer, pattern, RegexOptions.IgnoreCase);
                    if (isMatch)
                        break;
                }
            }
            if (!isMatch)
            {
                return Json(new { success = false, message = "Falsche Framenummer." });
            }
            // Call first api 

            var apiUrl = ConfigurationManager.AppSettings["OdooApiUrl"];
            var dbName = ConfigurationManager.AppSettings["OdooDatabase"];
            var userId = int.Parse(ConfigurationManager.AppSettings["OdooUserId"]);
            var apiKey = ConfigurationManager.AppSettings["OdooApiKey"];

            var getId = "";
            var client = new HttpClient();

            var requestBody = new
            {
                jsonrpc = "2.0",
                method = "call",
                @params = new
                {
                    service = "object",
                    method = "execute_kw",
                    args = new object[]
                    {
                           dbName, // From config
                           userId, // From config
                           apiKey, // From config
                            "stock.lot",
                            "search_read",
                           new object[]
                            {
                              new object[]
                                  {
                                     new object[] {"name", "=", bikeId}
                                  },
                              new string[] {"id","name","product_id","status","frame_number"}
                              },
                            new { }
                    }
                }
            };

            var content = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            try
            {
                var response = client.PostAsync(apiUrl, content).Result;

                if (!response.IsSuccessStatusCode)
                {
                    return Json(new { success = false, message = $"HTTP Error: {(int)response.StatusCode} - {response.ReasonPhrase}" });
                }

                var responseString = response.Content.ReadAsStringAsync().Result;
                var rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(responseString);

                if (rpcResponse.Error != null && rpcResponse.Error.HasValues)
                {
                    return Json(new { success = false, message = $"RPC Error: {rpcResponse.Error}" });
                }

                // response is successful → extract the id
                if (rpcResponse.Result != null)
                {
                    // Assuming Result is a JArray of objects
                    var resultArray = rpcResponse.Result as JArray;
                    if (resultArray != null && resultArray.Count > 0)
                    {
                        var firstItem = resultArray[0];
                        getId = firstItem["id"]?.ToString();


                    }
                }


            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Exception: {ex.Message}" });
            }



            // Update Frame number and  status


            try
            {
                var clientTwo = new HttpClient();
                var requestTwo = new HttpRequestMessage(HttpMethod.Post, apiUrl);

                var contentTwo = new StringContent(
                                 $@"{{
                                           ""jsonrpc"": ""2.0"",
                                           ""method"": ""call"",
                                           ""params"": {{
                                            ""service"": ""object"",
                                            ""method"": ""execute_kw"",
                                             ""args"": [
                                               ""{dbName}"",
                                             {userId},
                                            ""{apiKey}"",
                                                 ""stock.lot"",
                                            ""write"",
                                            [
                                              [{getId}],
                                              {{
                                             
                                              ""frame_number"": ""{rahmennummer}""
                                                   }}
                                            ]
                                         ]
                                            }},
                                              ""id"": 2
                                             }}",
                                      Encoding.UTF8,
                                   "application/json"
                );

                requestTwo.Content = contentTwo;

                var response = client.SendAsync(requestTwo).Result;

                if (!response.IsSuccessStatusCode)
                {
                    return Json(new { success = false, message = $"HTTP Error: {(int)response.StatusCode} - {response.ReasonPhrase}" });
                }

                var responseString = response.Content.ReadAsStringAsync().Result;
                var rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(responseString);

                if (rpcResponse.Error != null && rpcResponse.Error.HasValues)
                {
                    return Json(new { success = false, message = $"RPC Error: {rpcResponse.Error}" });
                }

                // "result": true → update succeeded
                if (rpcResponse.Result != null && rpcResponse.Result.ToString().ToLower() == "true")
                {

                }


            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Exception: {ex.Message}" });
            }

            // Save bike data into Emporon DB
            try
            {
                string connectionString = ConfigurationManager.ConnectionStrings["EmporonDBConnection"].ConnectionString;

                string navTableName = ConfigurationManager.AppSettings["SerializedProductsTable"];
                string navReplTableName = ConfigurationManager.AppSettings["SerializedProductsReplTable"];

                string updateQuery = $@"
                                     UPDATE {navTableName}
                                     SET frame_num = @frame_num,
                                          battery_num = @battery_num,
                                          accessory_bag = @accessory_bag
                                      WHERE serial_num = @serial_num;
                                     ";

                using (var connection = new SqlConnection(connectionString))
                using (var command = new SqlCommand(updateQuery, connection))
                {
                    command.Parameters.AddWithValue("@serial_num", bikeId);
                    command.Parameters.AddWithValue("@frame_num", rahmennummer);
                    command.Parameters.AddWithValue("@battery_num", akku);
                    command.Parameters.AddWithValue("@accessory_bag", zubehor);

                    connection.Open();
                    command.ExecuteNonQuery();
                }

                


                string updateQueryTwo = $@"
                                     UPDATE {navReplTableName}
                                     SET frame_num = @frame_num,
                                         battery_num = @battery_num,
                                         accessory_bag = @accessory_bag
                                      WHERE serial_num = @serial_num;
                                     ";

                using (var connection = new SqlConnection(connectionString))
                using (var commandTwo = new SqlCommand(updateQueryTwo, connection))
                {
                    commandTwo.Parameters.AddWithValue("@serial_num", bikeId);
                    commandTwo.Parameters.AddWithValue("@frame_num", rahmennummer);
                    commandTwo.Parameters.AddWithValue("@battery_num", akku);
                    commandTwo.Parameters.AddWithValue("@accessory_bag", zubehor);

                    connection.Open();
                    commandTwo.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fehler beim Speichern in die Emporon-Datenbank: " + ex.Message });
            }

            
            getBikepassData.Frame_Num = rahmennummer;
            getBikepassData.KeyNumber = key;
            getBikepassData.BatteryNumber = akku;
            getBikepassData.AccesoryBag = zubehor;

            var bikePlacementData = db.tbl_BikePlacements.Where(u => u.BikeID == bikeId && u.Status == "montiert").FirstOrDefault();
            if (bikePlacementData == null)
            {
                return Json(new { success = false, message = "Keine Daten für diese Seriennummer gefunden." });
            }
            bikePlacementData.KeyNumber = key;

            db.SaveChanges();

            return Json(new { success = true, message = $"Rahmennummer {rahmennummer} received successfully." });
        }




        [HttpPost]
        public JsonResult InCompleteJob(string bikeId)
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



            var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
            var updateBikeData = db.tbl_BikePlacements.Where(u => u.BikeID == bikeId && u.CompanyName == userCompany && u.AssemblerID == getUserData.UserId && u.Status == "Assigned" && u.Location == getUserData.BranchName).FirstOrDefault();

            var direktMontageBikeData = db.tbl_BikePlacements
               .FirstOrDefault(u => u.BikeID == bikeId && u.CompanyName == userCompany && u.AssemblerID == getUserData.UserId && u.Status == "DirektMontage" && u.Location == getUserData.BranchName);


            if (updateBikeData == null && direktMontageBikeData == null)
            {
                return Json(new { success = false, message = "Fahrrad nicht gefunden oder bereits montiert." });
            }





            string subject = $"Fahrrad Daten nicht korrekt";

            string body = $@"
                           <p style='font-family:Segoe UI, sans-serif; font-size:16px;'>
                           Hallo,<br><br>
                           die Produktdaten vom Fahrrad {"[ " + bikeId + " ]"} montiert von {"[ " + userName + " ]"}
                            stimmen nicht mit dem System überein.<br><br>

                             Bitte überprüfe das Rad. <br><br>
                           
                            Mit freundlichen Grüßen!
                     </p>";

            bool isProduction = Convert.ToBoolean(ConfigurationManager.AppSettings["IsProduction"]);
            string location = getUserData.BranchName?.ToUpper();
            var GetRecieverData = db.tbl_Branches.FirstOrDefault(u => u.BranchName.ToUpper() == location);

            try
            {
                using (MailMessage mail = new MailMessage())
                {
                    mail.From = new MailAddress("scanner@fahrrad-xxl.de");

                    if (!isProduction)
                    {
                        mail.To.Add("odoo@fahrrad-xxl.de");
                    }
                    else
                    {
                        string recipients = GetRecieverData?.Email ?? "odoo@fahrrad-xxl.de";

                        // Split by comma or semicolon and trim each
                        var addresses = recipients.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var addr in addresses)
                        {
                            mail.To.Add(addr.Trim());
                        }
                    }

                    mail.Bcc.Add("odoo@fahrrad-xxl.de");

                    mail.Subject = subject;
                    mail.Body = body;
                    mail.IsBodyHtml = true;

                    using (SmtpClient client = new SmtpClient("192.168.94.254", 25))
                    {
                        client.Credentials = new NetworkCredential("scanner@fahrrad-xxl.de", "jkJyOpPAoN16ohIU");
                        client.EnableSsl = false;
                        client.Send(mail);
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fehler beim Senden – bitte erneut versuchen. " });
            }

            if (updateBikeData != null)
            {
                updateBikeData.AssembleEndTime = DateTime.Now;
                updateBikeData.Status = "unmontiert";
            }
            else
            {

                if (direktMontageBikeData != null)
                {
                    direktMontageBikeData.AssembleEndTime = DateTime.Now;
                    direktMontageBikeData.Status = "unmontiert";

                }

            }

            db.SaveChanges();


            return Json(new { success = true, message = "Meldung gesendet – der Teamleiter wurde informiert." });
        }

        [HttpPost]
        public JsonResult CancelJob(string bikeId)
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



            var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
            var updateBikeData = db.tbl_BikePlacements.Where(u => u.BikeID == bikeId && u.CompanyName == userCompany && u.AssemblerID == getUserData.UserId && u.Status == "DirektMontage" && u.Location == getUserData.BranchName).FirstOrDefault();

            

            if (updateBikeData != null)
            {
                updateBikeData.Status = "abbrechen";
                db.SaveChanges();
            }
            else
            {
                return Json(new { success = false, message = "Fahrraddaten nicht gefunden." });
            }
            


            return Json(new { success = true, message = "Der Auftrag wurde abgebrochen." });
        }
        [HttpPost]
        public ActionResult RePrintFahrradPass(FormCollection form)
        {
            //string userName = Session["UserName"].ToString();
            //string userCompany = Session["CompanyName"].ToString();

            //string userName = "sajid";
            //string userCompany = "Emporon";
            if (!User.Identity.IsAuthenticated)
            {
                // cookie expired → force re-login

                return new HttpStatusCodeResult(500, "Die Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.");
            }

            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            string rahmennummer = form["rahmennummer"];
            string schluesselnummer = form["Schlüsselnummer"];
            string akkuNummer = form["Akkunummer"];
            string zubehoerTasche = form["zubehoerTasche"];
            string gabelNummer = form["gabelNummer"];
            string bikeId = form["bikeId"];
            string barcode = form["barcode"];

            string preview = form["preview"];
            string bearbeiter = form["bearbeiterName"];




            //var getBikeData = db.vw_NeumoProductDetails.Where(u => u.SerialNo == bikeId && u.cr023_unternehmen == userCompany).FirstOrDefault();

            var json = Session["getBikeData"] as string;
            var getBikeData = JsonConvert.DeserializeObject<vw_NeumoProductDetails>(json);

            var getImage = db.tbl_BrandExtension.Where(u => u.Code.ToLower() == getBikeData.Brand.ToLower()).FirstOrDefault();
            byte[] pictureBytes = null;
           

            if (getBikeData.Brand.ToLower() == "carver")
            {
                string path = Server.MapPath("~/Content/Images/carver.png");
                if (System.IO.File.Exists(path))
                {
                    pictureBytes = System.IO.File.ReadAllBytes(path);
                }
                   
            }
            else if (getBikeData.Brand.ToLower() == "diamant")
            {
                string path = Server.MapPath("~/Content/Images/diamant.png");
                if (System.IO.File.Exists(path))
                {
                    pictureBytes = System.IO.File.ReadAllBytes(path);
                }
                    
            }
            else if (getImage != null && getImage.Picture != null && getImage.Picture.Length > 0)
            {
                pictureBytes = getImage.Picture;  // send image
            }
            string qrImagepath = Server.MapPath("~/Content/Images/qrcode.png");
            string pdfPath = "";
            if (userCompany == "Emporon")
            {
                pdfPath = GenerateEmporonFahrradPassPdf(
                    brand: getBikeData.Brand,
                    logoImage: pictureBytes,
                    qrImage: qrImagepath,
                    modelName: getBikeData.Description,
                kategorie: getBikeData.Item_Category_Code,
                motor: getBikeData.E_Bike_Engine_Beschreibung,
                rahmenmaterial: getBikeData.Frame_Material,
                schaltung: getBikeData.Gearshift,
                radgroesse: getBikeData.Wheel_Size,
                rahmenhoehe: getBikeData.Frame_Height,
                farbe: getBikeData.ColourForName,
                akku: getBikeData.Leistung,
                box: zubehoerTasche,
                seriennummer: bikeId,
                fahrradPassId: "FP-2025-0001",
                artikelNr: getBikeData.Bar_Code,
                rahmenNummer: rahmennummer,
                batterieNummer: akkuNummer,
                schluesselNummer: schluesselnummer,
                erfa: getBikeData.ERFA_No_,
                employeeNumber: bearbeiter
                );
            }
            else
            {
                pdfPath = GenerateFahrradPassPdf(
                kategorie: getBikeData.Item_Category_Code,
                rahmentyp: getBikeData.Frame_Type,
                rahmenmaterial: getBikeData.Frame_Material,
                schaltung: getBikeData.Gearshift,
                radgroesse: getBikeData.Wheel_Size,
                rahmenhoehe: getBikeData.Frame_Height,
                farbe: getBikeData.ColourForName,
                akku: getBikeData.E_Bike_Engine,
                box: zubehoerTasche,
                seriennummer: bikeId,
                fahrradPassId: "FP-2025-0001",
                artikelNr: getBikeData.Bar_Code,
                rahmenNummer: rahmennummer,
                batterieNummer: akkuNummer,
                schluesselNummer: schluesselnummer

                 );
            }


            try
            {
                if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
                {
                    if(preview == "1")
                    {
                        byte[] fileBytes = System.IO.File.ReadAllBytes(pdfPath);
                        return File(fileBytes, "application/pdf", Path.GetFileName(pdfPath));
                    }
                    else
                    {
                        var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
                        byte[] fileBytes = System.IO.File.ReadAllBytes(pdfPath);

                        if (getUserData == null || string.IsNullOrWhiteSpace(getUserData.BikepassPrinter))
                        {
                            // ❌ No printer set → return file directly to browser
                            return File(fileBytes, "application/pdf", Path.GetFileName(pdfPath));
                        }
                        else
                        {
                            // ✅ Printer available → prepare and save
                            string printerName = getUserData.BikepassPrinter.Trim();
                            string timestamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff");
                            string fileName = $"[{printerName}]_NeuMo_{timestamp}.pdf";

                            string targetFolder = @"\\MS-FXXL-55-W19\PDFToPrint\Test";
                            string fullPath = Path.Combine(targetFolder, fileName);

                            try
                            {
                                // Save file
                                System.IO.File.WriteAllBytes(fullPath, fileBytes);

                                // Insert DB
                                Insert(fullPath, printerName);

                                return Json(new { success = true });
                            }
                            catch (Exception ex)
                            {
                                // Return JSON error
                                return Json(new { success = false, message = "Daten konnten nicht gespeichert werden.", detail = ex.Message });
                            }
                        }
                    }
                   
                }
                else
                {
                    return new HttpStatusCodeResult(500, "PDF file missing or not generated.");
                }
            }
            catch (Exception ex)
            {
                return new HttpStatusCodeResult(500, "Unexpected error: " + ex.Message);
            }


        }


        [HttpPost]
        public ActionResult GenerateFahrradPass(FormCollection form)
        {
            //string userName = Session["UserName"].ToString();
            //string userCompany = Session["CompanyName"].ToString();

            //string userName = "sajid";
            //string userCompany = "Emporon";
            if (!User.Identity.IsAuthenticated)
            {
                // cookie expired → force re-login

                return new HttpStatusCodeResult(500, "Die Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.");
            }

            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            string rahmennummer = form["rahmennummer"];
            string schluesselnummer = form["Schlüsselnummer"];
            string akkuNummer = form["Akkunummer"];
            string zubehoerTasche = form["zubehoerTasche"];
            string gabelNummer = form["gabelNummer"];
            string bikeId = form["bikeId"];
            string barcode = form["barcode"];

            string mechanicName = form["mechanicDropdown"];

            var userData = db.Users.FirstOrDefault(u => u.UserName == mechanicName);

            


            var getBikeData = db.vw_NeumoProductDetails.Where(u => u.Bar_Code == barcode && u.Company == userCompany).FirstOrDefault();

            var getImage = db.tbl_BrandExtension.Where(u => u.Code.ToLower() == getBikeData.Brand.ToLower()).FirstOrDefault();
            byte[] pictureBytes = null;

            if (getBikeData.Brand.ToLower() == "carver")
            {
                string path = Server.MapPath("~/Content/Images/carver.png");
                if (System.IO.File.Exists(path))
                    pictureBytes = System.IO.File.ReadAllBytes(path);
            }
            else if (getBikeData.Brand.ToLower() == "diamant")
            {
                string path = Server.MapPath("~/Content/Images/diamant.png");
                if (System.IO.File.Exists(path))
                    pictureBytes = System.IO.File.ReadAllBytes(path);
            }
            else if (getImage != null && getImage.Picture != null && getImage.Picture.Length > 0)
            {
                pictureBytes = getImage.Picture;  // send image
            }

            string qrImagepath = Server.MapPath("~/Content/Images/qrcode.png");
            string pdfPath = "";
            if (userCompany == "Emporon")
            {
                pdfPath = GenerateEmporonFahrradPassPdf(
                    brand: getBikeData.Brand,
                     logoImage: pictureBytes,
                     qrImage: qrImagepath,
                    modelName: getBikeData.Description,
                kategorie: getBikeData.Item_Category_Code,
                motor: getBikeData.E_Bike_Engine_Beschreibung,
                rahmenmaterial: getBikeData.Frame_Material,
                schaltung: getBikeData.Gearshift,
                radgroesse: getBikeData.Wheel_Size,
                rahmenhoehe: getBikeData.Frame_Height,
                farbe: getBikeData.ColourForName,
                akku: getBikeData.Leistung,
                box: zubehoerTasche,
                seriennummer: bikeId,
                fahrradPassId: "FP-2025-0001",
                artikelNr: getBikeData.Bar_Code,
                rahmenNummer: rahmennummer,
                batterieNummer: akkuNummer,
                schluesselNummer: schluesselnummer,
                erfa : getBikeData.ERFA_No_,
                employeeNumber : userData.EmployeeNumber
                );
            }
            else
            {
                pdfPath = GenerateFahrradPassPdf(
               kategorie: getBikeData.Item_Category_Code,
                rahmentyp: getBikeData.Frame_Type,
                rahmenmaterial: getBikeData.Frame_Material,
                schaltung: getBikeData.Gearshift,
                radgroesse: getBikeData.Wheel_Size,
                rahmenhoehe: getBikeData.Frame_Height,
                farbe: getBikeData.ColourForName,
                akku: getBikeData.E_Bike_Engine,
                box: zubehoerTasche,
                seriennummer: bikeId,
                fahrradPassId: "FP-2025-0001",
                artikelNr: getBikeData.Bar_Code,
                rahmenNummer: rahmennummer,
                batterieNummer: akkuNummer,
                schluesselNummer: schluesselnummer
                 );
            }

            if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
            {

                // byte[] fileBytes = System.IO.File.ReadAllBytes(pdfPath);
                //return File(fileBytes, "application/pdf", Path.GetFileName(pdfPath));


                // new code for printing
               
                var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
                byte[] fileBytes = System.IO.File.ReadAllBytes(pdfPath);

                if (getUserData == null || string.IsNullOrWhiteSpace(getUserData.BikepassPrinter))
                {
                    // ❌ No printer set → return file directly to browser
                    return File(fileBytes, "application/pdf", Path.GetFileName(pdfPath));
                }
                else
                {
                    // ✅ Printer available → prepare and save
                    string printerName = getUserData.BikepassPrinter.Trim();
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff");
                    string fileName = $"[{printerName}]_NeuMo_{timestamp}.pdf";

                    string targetFolder = @"\\MS-FXXL-55-W19\PDFToPrint\";
                    string fullPath = Path.Combine(targetFolder, fileName);

                    try
                    {
                        // Save file
                        System.IO.File.WriteAllBytes(fullPath, fileBytes);

                        // Insert DB
                        Insert(fullPath, printerName);

                        return Json(new { success = true });
                    }
                    catch (Exception ex)
                    {
                        // Return JSON error
                        return Json(new { success = false, message = "Daten konnten nicht gespeichert werden.", detail = ex.Message });
                    }
                }
            }
            else
            {
                return new HttpStatusCodeResult(500, "PDF file missing or not generated.");
            }


        }
        public int Insert(string filename, string printerName)
        {
            if (string.IsNullOrEmpty(filename))
            {
                throw new ArgumentException("Parameter must not be null or empty.", nameof(filename));
            }

            if (string.IsNullOrEmpty(printerName))
            {
                throw new ArgumentException("Parameter must not be null or empty.", nameof(printerName));
            }

            string sql = @"
        INSERT INTO [dbo].[PrintQueue] 
            ([PrinterName], [Filename], [LastStatus], [AddedToQueue], [LastStatusUpdate], [UncPath]) 
        VALUES 
            (@printerName, @filename, @lastStatus, @addedToQueue, @lastStatusUpdate, @uncPath)";

            // read connection string from config
            string connectionString = ConfigurationManager.ConnectionStrings["DBConnectPrintService"].ConnectionString;

            using (SqlConnection conn = new SqlConnection(connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@printerName", printerName);
                cmd.Parameters.AddWithValue("@filename", filename);
                cmd.Parameters.AddWithValue("@lastStatus", "AddedToQueue");
                cmd.Parameters.AddWithValue("@addedToQueue", DateTime.Now);
                cmd.Parameters.AddWithValue("@lastStatusUpdate", DateTime.Now);
                cmd.Parameters.AddWithValue("@uncPath", filename);

                conn.Open();
                return cmd.ExecuteNonQuery(); // returns number of rows inserted (1 if success)
            }
        }

        public static string GenerateEmporonFahrradPassPdf(
        string brand, byte[] logoImage, string qrImage, string modelName, string kategorie, string motor, string rahmenmaterial, string schaltung, string radgroesse,
        string rahmenhoehe, string farbe, string akku, string box, string seriennummer, string fahrradPassId,
        string artikelNr, string rahmenNummer, string batterieNummer, string schluesselNummer, string erfa , string employeeNumber
    )
        {
            string fileName = $"FahrradPass_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string tempFilePath = Path.Combine(Path.GetTempPath(), fileName);

            using (FileStream fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // A5 Landscape (so it can be folded into A6 booklet)
                Rectangle a5Landscape = PageSize.A5.Rotate();
                Document doc = new Document(a5Landscape, 20f, 20f, 20f, 0f);
                PdfWriter writer = PdfWriter.GetInstance(doc, fs);
                doc.Open();

                // Fonts (only black, since red text is already printed on paper)
                var labelFont = FontFactory.GetFont(FontFactory.HELVETICA, 10, BaseColor.BLACK);
                var valueFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10, BaseColor.BLACK);

                // Main table with two columns (left & right halves)
                PdfPTable mainTable = new PdfPTable(2) { WidthPercentage = 100 };
                mainTable.SetWidths(new float[] { 1f, 1f });

                // ---------- LEFT SIDE (Cover, boxes only) ----------
                PdfPTable leftTable = new PdfPTable(1) { WidthPercentage = 100 };
                PdfContentByte cb = writer.DirectContent;
                // add top empty space for red FAHRRAD-PASS
                leftTable.AddCell(new PdfPCell(new Phrase(""))
                {
                    Border = Rectangle.NO_BORDER,
                    FixedHeight = 56f // adjust this height depending on your print template
                });

                var qrValueString = "Zur Berechnung deiner Leasingrate scanne den Code:";
                // ---------------------------------------------
                // 1) QR BOX (half width of X-Nummer row)
                // ---------------------------------------------
                PdfPTable qrRow = new PdfPTable(2)   // 2 columns: QR (50%) + empty (50%)
                {
                    WidthPercentage = 100
                };
                qrRow.SetWidths(new float[] { 0.6f, 1f });

                // Load QR/barcode image from file path
                iTextSharp.text.Image qrImg = iTextSharp.text.Image.GetInstance(qrImage);

                // Scale QR image
                qrImg.ScaleToFit(30f, 30f);
                qrImg.Alignment = Element.ALIGN_CENTER;

                // Create cell with value + QR image
                PdfPCell qrCell = new PdfPCell()
                {
                    Border = Rectangle.NO_BORDER,
                    Padding = 2f,
                    HorizontalAlignment = Element.ALIGN_CENTER
                };

                var qrDescriptionFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 6, BaseColor.BLACK);
                // Add the VALUE text (shown above QR code)
                qrCell.AddElement(new Paragraph(qrValueString, qrDescriptionFont)
                {
                    Alignment = Element.ALIGN_CENTER,
                    SpacingAfter = 5f
                });

                // Add QR image
                qrCell.AddElement(qrImg);

                // Add QR box (left 50%)
                qrRow.AddCell(qrCell);

                // Right 50% empty
                qrRow.AddCell(new PdfPCell(new Phrase(""))
                {
                    Border = Rectangle.NO_BORDER
                });

                // Add QR row to main left table
                leftTable.AddCell(new PdfPCell(qrRow)
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingBottom = 5f
                });


                PdfPTable emptyRow = new PdfPTable(2)
                {
                    WidthPercentage = 100,
                    SpacingAfter = 8f
                };

                // Set both columns to equal width
                emptyRow.SetWidths(new float[] { 1f, 1f });

                // -----------------------
                // Left box: Seriennummer
                // -----------------------
                PdfPCell serienCell = new PdfPCell(MakeLabelValueBox("Seriennummer", seriennummer, labelFont, valueFont))
                {
                    Border = Rectangle.NO_BORDER,
                    //PaddingRight = 5f // space between the two boxes
                };
                //PdfPCell serienCell = CreateBarcodeCell("Seriennummer", seriennummer, cb, labelFont);
                ////serienCell.PaddingRight = 5f;
                emptyRow.AddCell(serienCell);

                // -----------------------
                // Right box: new box (same size as Seriennummer)
                // -----------------------
                //PdfPCell newBoxCell = new PdfPCell(MakeLabelValueBox("X-Nummer", erfa, labelFont, valueFont))
                //{
                //    Border = Rectangle.NO_BORDER,
                //    PaddingLeft = 5f
                //};

                //emptyRow.AddCell(newBoxCell);

                PdfPCell xNumberCell = CreateBarcodeCell("X-Nummer", erfa, cb, labelFont);
                xNumberCell.PaddingLeft = 5f;
                emptyRow.AddCell(xNumberCell);
                // -----------------------
                // Add the row to the leftTable
                // -----------------------
                leftTable.AddCell(new PdfPCell(emptyRow)
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingBottom = 0f
                });


                // Akku-Nummer + Artikel Nr.
                // Create the main table with 2 columns

                PdfPTable row1 = new PdfPTable(2)
                {
                    WidthPercentage = 100,
                    SpacingAfter = 8f
                };
                row1.SetWidths(new float[] { 1f, 1f });






                // -----------------------
                // Akku-Nummer heading (outside the box)
                // -----------------------

                //row1.AddCell(CreateBarcodeCell("Akku-Nummer", batterieNummer, cb, labelFont));
                row1.AddCell(CreateQrCodeCell("Akku-Nummer", batterieNummer, cb, labelFont));

                
                // -----------------------
                // Artikel Nr. box
                // -----------------------
                PdfPCell artikelCell = new PdfPCell(MakeLabelValueBox("Artikel Nr.", artikelNr, labelFont, valueFont))
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingLeft = 5f
                };
                row1.AddCell(artikelCell);

                // -----------------------
                // Add row to leftTable
                // -----------------------
                leftTable.AddCell(new PdfPCell(row1) { Border = Rectangle.NO_BORDER, PaddingBottom = 0f });


                // Rahmen-Nummer + Schlüssel-Nummer
                PdfPTable row2 = new PdfPTable(2) { WidthPercentage = 100, SpacingAfter = 8f };
                row2.SetWidths(new float[] { 1f, 1f });

                //row2.AddCell(new PdfPCell(MakeLabelValueBox("Rahmen-Nummer", rahmenNummer, labelFont, valueFont))
                //{
                //    Border = Rectangle.NO_BORDER,
                //    //PaddingRight = 5f
                //});
                row2.AddCell(CreateBarcodeCell("Rahmen-Nummer", rahmenNummer, cb, labelFont));

                row2.AddCell(new PdfPCell(MakeLabelValueBox("Schlüssel-Nummer", schluesselNummer, labelFont, valueFont))
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingLeft = 5f
                });

                leftTable.AddCell(new PdfPCell(row2) { Border = Rectangle.NO_BORDER, PaddingBottom = 0f });

                // Zubehör row
                PdfPTable row3 = new PdfPTable(2) { WidthPercentage = 100, SpacingAfter = 8f };
                row3.SetWidths(new float[] { 1f, 1f });

                // First column (Zubehör)
                row3.AddCell(new PdfPCell(MakeLabelValueBox("Zubehör", box, labelFont, valueFont))
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingRight = 5f
                });

                row3.AddCell(new PdfPCell(MakeLabelValueBox("Monteur", employeeNumber, labelFont, valueFont))
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingLeft = 5f
                });

                leftTable.AddCell(new PdfPCell(row3) { Border = Rectangle.NO_BORDER, PaddingBottom = 0f });

                


                // ---------- RIGHT SIDE (Inside details, label–value layout) ----------
                PdfPTable rightTable = new PdfPTable(1) { WidthPercentage = 100 };

                // add top empty space for red FAHRRAD-PASS
                rightTable.AddCell(new PdfPCell(new Phrase(""))
                {
                    Border = Rectangle.NO_BORDER,
                    FixedHeight = 40f // adjust to match your printed template
                });

                // Make the label font bold
                var boldLabelFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 12);
                // Brand
                //rightTable.AddCell(MakeLabelValue("Marke", brand, boldLabelFont, valueFont));

                // BRAND ROW (no heading, only image OR brand text)
                if (logoImage != null && logoImage.Length > 0)
                {

                    // Convert byte[] to image
                    iTextSharp.text.Image brandImg = iTextSharp.text.Image.GetInstance(logoImage);

                    brandImg.ScaleToFit(170f, 350f);  // adjust size
                    brandImg.Alignment = Element.ALIGN_LEFT;

                    PdfPCell imgCell = new PdfPCell(brandImg)
                    {
                        Border = Rectangle.NO_BORDER,
                        PaddingTop = 0f,
                        PaddingBottom = 0f,
                        HorizontalAlignment = Element.ALIGN_LEFT
                    };

                    rightTable.AddCell(imgCell);

                }
                else
                {
                    rightTable.AddCell(MakeLabelValue("Marke", brand, boldLabelFont, boldLabelFont));

                    
                }

                // small spacing row
                rightTable.AddCell(new PdfPCell(new Phrase(""))
                {
                    Border = Rectangle.NO_BORDER,
                    FixedHeight = 2f
                });

                // Modell
                rightTable.AddCell(MakeLabelValue("Modell", modelName, boldLabelFont, boldLabelFont));

                // small spacing row
                rightTable.AddCell(new PdfPCell(new Phrase(""))
                {
                    Border = Rectangle.NO_BORDER,
                    FixedHeight = 6f
                });
                rightTable.AddCell(MakeLabelValue("Motor", motor, labelFont, valueFont));
                rightTable.AddCell(MakeLabelValue("Kategorie", kategorie, labelFont, valueFont));
                //rightTable.AddCell(MakeLabelValue("Rahmentyp", rahmentyp, labelFont, valueFont));
                rightTable.AddCell(MakeLabelValue("Rahmenmaterial", rahmenmaterial, labelFont, valueFont));
                rightTable.AddCell(MakeLabelValue("Schaltung", schaltung, labelFont, valueFont));
                rightTable.AddCell(MakeLabelValue("Radgröße", radgroesse, labelFont, valueFont));
                rightTable.AddCell(MakeLabelValue("Farbe", farbe, labelFont, valueFont));
                rightTable.AddCell(MakeLabelValue("Akku-Kapazität", akku, labelFont, valueFont));
                rightTable.AddCell(MakeLabelValue("Rahmenhöhe", rahmenhoehe, labelFont, valueFont));

                // -------- Small footnotes section --------
                var smallFont = FontFactory.GetFont(FontFactory.HELVETICA, 6, BaseColor.BLACK);

                // Note 1
                rightTable.AddCell(new PdfPCell(new Phrase("*: unverbindliche Preisempfehlung des Herstellers", smallFont))
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingTop = 2f
                });

                // Note 2
                rightTable.AddCell(new PdfPCell(new Phrase("**: bei Sofortzahlung in Bar oder Karte", smallFont))
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingTop = 2f
                });

                // Note 3 (multiline)
                rightTable.AddCell(new PdfPCell(new Phrase(
                    "***: Monatliche Leasingrate Laufzeit 36 Monate\n" +
                    "Beispielrechnung für eine durchschnittliche Leasingrate\n" +
                    "abhängig von Leasinggesellschaft, Steuerklasse, Anzahl Kinder,\n" +
                    "Kirchensteuer, Bruttomonatsgehalt\n" +
                    "durchschnittliche Ersparnis ggü. Barkauf ca. 30%",
                    smallFont))
                {
                    Border = Rectangle.NO_BORDER,
                    PaddingTop = 2f
                });

                // Add to main table
                mainTable.AddCell(new PdfPCell(leftTable) { Border = Rectangle.NO_BORDER, Padding = 10f });
                mainTable.AddCell(new PdfPCell(rightTable) { Border = Rectangle.NO_BORDER, Padding = 10f });

                doc.Add(mainTable);
                doc.Close();
            }

            return tempFilePath;
        }

        // Helpers
        private static PdfPCell MakeLabelValue(string label, string value, Font labelFont, Font valueFont)
        {
            PdfPTable inner = new PdfPTable(2) { WidthPercentage = 100 };
            inner.SetWidths(new float[] { 1f, 2f });

            inner.AddCell(new PdfPCell(new Phrase(label + ":", labelFont)) { Border = Rectangle.NO_BORDER, PaddingBottom = 0f });
            inner.AddCell(new PdfPCell(new Phrase(value ?? "", valueFont)) { Border = Rectangle.NO_BORDER, PaddingBottom = 0f });

            return new PdfPCell(inner) { Border = Rectangle.NO_BORDER, PaddingBottom = 4f };
        }

        private static PdfPCell MakeLabelValueBox(string label, string value, Font labelFont, Font valueFont)
        {
            PdfPTable inner = new PdfPTable(1) { WidthPercentage = 100 };
            inner.AddCell(new PdfPCell(new Phrase(label, labelFont))
            {
                Border = Rectangle.NO_BORDER,
                PaddingBottom = 2f
            });

            inner.AddCell(new PdfPCell(new Phrase(value ?? "", valueFont))
            {
                Border = Rectangle.BOX,
                FixedHeight = 30f,
                HorizontalAlignment = Element.ALIGN_CENTER,
                VerticalAlignment = Element.ALIGN_MIDDLE
            });

            return new PdfPCell(inner)
            {
                Border = Rectangle.NO_BORDER,
                PaddingBottom = 10f // space between rows
            };
        }
        private static PdfPCell CreateEmporonBarcodeCell(string title, string value, PdfContentByte cb, iTextSharp.text.Font labelFont)
        {
            PdfPTable inner = new PdfPTable(1) { WidthPercentage = 100 };

            // Title
            PdfPCell titleCell = new PdfPCell(new Phrase(title, labelFont))
            {
                Border = iTextSharp.text.Rectangle.NO_BORDER,
                PaddingBottom = 4f
            };
            inner.AddCell(titleCell);

            // Value cell instead of barcode
            PdfPCell valueCell = new PdfPCell(new Phrase(value ?? "", FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9)))
            {
                Border = iTextSharp.text.Rectangle.BOX,
                HorizontalAlignment = Element.ALIGN_CENTER,
                VerticalAlignment = Element.ALIGN_MIDDLE,
                FixedHeight = 40f,
                Padding = 4f
            };
            inner.AddCell(valueCell);

            return new PdfPCell(inner)
            {
                Border = iTextSharp.text.Rectangle.NO_BORDER,
                Padding = 4f
            };
        }


        public static string GenerateFahrradPassPdf(
    string kategorie,
    string rahmentyp,
    string rahmenmaterial,
    string schaltung,
    string radgroesse,
    string rahmenhoehe,
    string farbe,
    string akku,
    string box,
    string seriennummer,
    string fahrradPassId,
    string artikelNr,
    string rahmenNummer,
    string batterieNummer,
    string schluesselNummer
)
        {
            string fileName = $"FahrradPass_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string tempFilePath = Path.Combine(Path.GetTempPath(), fileName);

            using (FileStream fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                iTextSharp.text.Rectangle quarterA4 = new iTextSharp.text.Rectangle(297.5f, 421f);
                Document doc = new Document(quarterA4, 18f, 18f, 4f, 0f);
                PdfWriter writer = PdfWriter.GetInstance(doc, fs);
                doc.Open();
                PdfContentByte cb = writer.DirectContent;

                var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 32, BaseColor.RED);
                var labelFont = FontFactory.GetFont(FontFactory.HELVETICA, 9, BaseColor.BLACK);
                var valueFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9, BaseColor.BLACK);

                doc.Add(new Paragraph("FAHRRAD-PASS", titleFont) { SpacingAfter = 15f });
                doc.Add(new Paragraph("\n"));
                PdfPTable CreateTable(int columns, float[] widths)
                {
                    var table = new PdfPTable(columns) { WidthPercentage = 100 };
                    table.SetWidths(widths);
                    return table;
                }

                void AddLabelValue(PdfPTable table, string label, string value)
                {
                    table.AddCell(new PdfPCell(new Phrase(label + ":", labelFont))
                    {
                        Border = iTextSharp.text.Rectangle.NO_BORDER,
                        PaddingBottom = 4f
                    });

                    table.AddCell(new PdfPCell(new Phrase(value, valueFont))
                    {
                        Border = iTextSharp.text.Rectangle.NO_BORDER,
                        PaddingBottom = 4f
                    });
                }

                // Basic Info
                var infoTable = CreateTable(2, new float[] { 1f, 2f });
                AddLabelValue(infoTable, "Kategorie", kategorie);
                AddLabelValue(infoTable, "Rahmentyp", rahmentyp);
                AddLabelValue(infoTable, "Rahmenmaterial", rahmenmaterial);
                AddLabelValue(infoTable, "Schaltung", schaltung);
                doc.Add(infoTable);

                // Radgröße & Rahmenhöhe
                var sizeTable = CreateTable(4, new float[] { 2f, 1f, 1.4f, 1.6f });
                AddLabelValue(sizeTable, "Radgröße", radgroesse);
                AddLabelValue(sizeTable, "Rahmenhöhe", rahmenhoehe);
                doc.Add(sizeTable);

                // Farbe
                var farbeTable = CreateTable(2, new float[] { 1f, 2f });
                AddLabelValue(farbeTable, "Farbe", farbe);
                doc.Add(farbeTable);

                // Akku & Box
                var powerTable = CreateTable(2, new float[] { 1f, 2f });
                AddLabelValue(powerTable, "Akku", akku);
                AddLabelValue(powerTable, "Zubehör", box);
                doc.Add(powerTable);



                // Seriennummer with barcode (inside a table box to prevent overflow)
                PdfPTable seriennummerTable = new PdfPTable(1) { WidthPercentage = 100 };
                seriennummerTable.SpacingBefore = 5f;

                // Label
                PdfPCell labelCell = new PdfPCell(new Phrase("Seriennummer", labelFont))
                {
                    Border = iTextSharp.text.Rectangle.NO_BORDER,
                    //HorizontalAlignment = Element.ALIGN_CENTER,
                    //PaddingBottom = 4f
                };
                seriennummerTable.AddCell(labelCell);

                if (!string.IsNullOrWhiteSpace(seriennummer))
                {
                    Barcode128 barcode = new Barcode128
                    {
                        Code = seriennummer,
                        CodeType = Barcode.CODE128,
                        BarHeight = 30f,
                        X = 0.8f
                    };

                    Image barcodeImg = barcode.CreateImageWithBarcode(cb, null, null);
                    barcodeImg.Alignment = Element.ALIGN_CENTER;
                    barcodeImg.ScaleToFit(250f, 40f); // prevent overflow

                    PdfPCell barcodeCell = new PdfPCell(barcodeImg)
                    {
                        Border = iTextSharp.text.Rectangle.BOX,
                        HorizontalAlignment = Element.ALIGN_CENTER,
                        Padding = 4f,
                        FixedHeight = 40f
                    };
                    seriennummerTable.AddCell(barcodeCell);

                    PdfPCell valueCell = new PdfPCell(new Phrase(seriennummer, valueFont))
                    {
                        Border = iTextSharp.text.Rectangle.NO_BORDER,
                        HorizontalAlignment = Element.ALIGN_CENTER,
                        PaddingTop = 4f
                    };
                    //seriennummerTable.AddCell(valueCell);
                }
                else
                {
                    seriennummerTable.AddCell(new PdfPCell(new Phrase("Keine Seriennummer verfügbar", labelFont))
                    {
                        Border = iTextSharp.text.Rectangle.BOX,
                        HorizontalAlignment = Element.ALIGN_CENTER,
                        FixedHeight = 40f
                    });
                }

                doc.Add(seriennummerTable);

                // Bottom Barcodes Table
                var bottomTable = CreateTable(2, new float[] { 1f, 1f });
                bottomTable.SpacingBefore = 2f;

                bottomTable.AddCell(CreateBarcodeCell("Batterie-Nr.", batterieNummer, cb, labelFont));
                bottomTable.AddCell(CreateBarcodeCell("Artikel Nr.", artikelNr, cb, labelFont));
                bottomTable.AddCell(CreateBarcodeCell("Rahmen-Nummer", rahmenNummer, cb, labelFont));
                bottomTable.AddCell(CreateBarcodeCell("Schlüssel-Nr.", schluesselNummer, cb, labelFont));

                doc.Add(bottomTable);

                doc.Close();
            }

            return tempFilePath;
        }

        private static PdfPCell CreateBarcodeCell(string title, string value, PdfContentByte cb, iTextSharp.text.Font labelFont)
        {
            PdfPTable inner = new PdfPTable(1) { WidthPercentage = 100 };

            PdfPCell titleCell = new PdfPCell(new Phrase(title, labelFont))
            {
                Border = iTextSharp.text.Rectangle.NO_BORDER,
                //HorizontalAlignment = Element.ALIGN_CENTER,
                PaddingBottom = 2f
            };
            inner.AddCell(titleCell);

            if (!string.IsNullOrWhiteSpace(value))
            {
                Barcode128 barcode = new Barcode128
                {
                    Code = value,
                    CodeType = Barcode.CODE128,
                    BarHeight = 20f,
                    X = 0.8f // Controls width of barcode bars
                };

                Image barcodeImg = barcode.CreateImageWithBarcode(cb, null, null);
                barcodeImg.ScaleToFit(120f, 30f); // LIMIT max width & height
                barcodeImg.Alignment = Element.ALIGN_CENTER;

                PdfPCell imgCell = new PdfPCell()
                {
                    Border = iTextSharp.text.Rectangle.BOX,
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    VerticalAlignment = Element.ALIGN_MIDDLE,
                    FixedHeight = 30f,
                    Padding = 2f
                };
                imgCell.AddElement(barcodeImg);
                inner.AddCell(imgCell);
            }
            else
            {
                inner.AddCell(new PdfPCell(new Phrase("", labelFont))
                {
                    Border = iTextSharp.text.Rectangle.BOX,
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    FixedHeight = 30f
                });
            }

            return new PdfPCell(inner)
            {
                Border = iTextSharp.text.Rectangle.NO_BORDER,
                PaddingBottom = 10f
            };
        }
        
        private static PdfPCell CreateQrCodeCell(string title, string value, PdfContentByte cb, iTextSharp.text.Font labelFont)
        {
            PdfPTable inner = new PdfPTable(1) { WidthPercentage = 100 };

            // Title
            PdfPCell titleCell = new PdfPCell(new Phrase(title, labelFont))
            {
                Border = iTextSharp.text.Rectangle.NO_BORDER,
                PaddingBottom = 2f
            };
            inner.AddCell(titleCell);

            if (!string.IsNullOrWhiteSpace(value))
            {
                // Create QR code (bigger now)
                BarcodeQRCode qr = new BarcodeQRCode(value, 150, 150, null);
                Image qrImg = qr.GetImage();

                // Make QR larger (fits inside 40px height)
                qrImg.ScaleToFit(28f, 28f);
                qrImg.Alignment = Element.ALIGN_CENTER;

                // Auto-shrink font for long values
                float maxWidth = 120f;
                float fontSize = labelFont.Size;
                Font shrinkingFont = new Font(labelFont);

                while (shrinkingFont.BaseFont.GetWidthPoint(value, fontSize) > maxWidth && fontSize > 4)
                {
                    fontSize -= 0.5f;
                }

                shrinkingFont.Size = fontSize;

                // Internal table: QR at top + value under it
                PdfPTable qrTable = new PdfPTable(1) { WidthPercentage = 100 };

                // QR cell (reduced padding top!)
                PdfPCell qrImgCell = new PdfPCell(qrImg)
                {
                    Border = iTextSharp.text.Rectangle.NO_BORDER,
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    VerticalAlignment = Element.ALIGN_TOP,
                    PaddingTop = 0f,
                    PaddingBottom = 0f
                };
                qrTable.AddCell(qrImgCell);

                // Value cell
                PdfPCell valueCell = new PdfPCell(new Phrase(value, shrinkingFont))
                {
                    Border = iTextSharp.text.Rectangle.NO_BORDER,
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    PaddingTop = 0f,
                    PaddingBottom = 0f
                };
                qrTable.AddCell(valueCell);

                // Outer box (now 40px tall)
                PdfPCell imgCell = new PdfPCell(qrTable)
                {
                    Border = iTextSharp.text.Rectangle.BOX,
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    VerticalAlignment = Element.ALIGN_MIDDLE,
                    FixedHeight = 40f,    // << Increased from 30f to 40f
                    Padding = 1f
                };

                inner.AddCell(imgCell);
            }
            else
            {
                inner.AddCell(new PdfPCell(new Phrase("", labelFont))
                {
                    Border = iTextSharp.text.Rectangle.BOX,
                    HorizontalAlignment = Element.ALIGN_CENTER,
                    FixedHeight = 40f
                });
            }

            return new PdfPCell(inner)
            {
                Border = iTextSharp.text.Rectangle.NO_BORDER,
                PaddingBottom = 10f
            };
        }



        private static PdfPCell CreateBarcodeCellNew(string title, string value, PdfContentByte cb, iTextSharp.text.Font labelFont)
        {
            // Outer table to stack title + barcode box
            PdfPTable outerTable = new PdfPTable(1) { WidthPercentage = 100 };

            // Title on top, outside the box
            if (!string.IsNullOrWhiteSpace(title))
            {
                PdfPCell titleCell = new PdfPCell(new Phrase(title, labelFont))
                {
                    Border = iTextSharp.text.Rectangle.NO_BORDER,
                    HorizontalAlignment = Element.ALIGN_LEFT,
                    PaddingBottom = 2f
                };
                outerTable.AddCell(titleCell);
            }

            // Barcode box with fixed height
            PdfPCell barcodeCell = new PdfPCell
            {
                Border = Rectangle.BOX,
                FixedHeight = 30f,
                HorizontalAlignment = Element.ALIGN_CENTER,
                VerticalAlignment = Element.ALIGN_MIDDLE,
                Padding = 0f
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                Barcode128 barcode = new Barcode128
                {
                    Code = value,
                    CodeType = Barcode.CODE128,
                    BarHeight = 15f,
                    X = 0.8f
                };

                Image barcodeImg = barcode.CreateImageWithBarcode(cb, null, null);
                barcodeImg.ScaleToFit(120f, 30f); // strictly fit inside 30f
                barcodeImg.Alignment = Element.ALIGN_CENTER;

                barcodeCell.AddElement(barcodeImg);
            }

            outerTable.AddCell(barcodeCell);

            // Return as a single cell
            PdfPCell finalCell = new PdfPCell(outerTable)
            {
                Border = iTextSharp.text.Rectangle.NO_BORDER,
                Padding = 0f
            };

            return finalCell;
        }



        [HttpPost]
        public ActionResult GenerateBarcodePdf(FormCollection form)
        {
            string barcodeValue = form["barcodeValue"];
            string headingValue = form["headingValue"];
            

            int dashIndex = headingValue.IndexOf('-');
            if (dashIndex >= 0 && dashIndex < headingValue.Length - 1)
            {
                headingValue = headingValue.Substring(dashIndex + 1).Trim();
            }
            else
            {
                headingValue = headingValue.Trim(); // fallback if no dash found
            }

            bool useQrCode = headingValue.StartsWith("Seriennummer", StringComparison.OrdinalIgnoreCase);

            using (var ms = new MemoryStream())
            {
                var pageSize = new Rectangle(288f, 96f);

                // margins depend on QR or barcode
                float left = 10f;
                float right = 10f;
                float top = useQrCode ? 0f : 5f;
                float bottom = useQrCode ? 0f : 5f;

                var document = new Document(pageSize, left, right, top, bottom);
                PdfWriter writer = PdfWriter.GetInstance(document, ms);

                document.Open();

                // ✅ 1. Add centered heading
                if (!useQrCode)
                {
                    var headingFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10);
                    var heading = new Paragraph(headingValue, headingFont)
                    {
                        Alignment = Element.ALIGN_CENTER,
                        SpacingAfter = 2f
                    };
                    document.Add(heading);
                }

                if (useQrCode)
                {
                    BarcodeQRCode qrCode = new BarcodeQRCode(barcodeValue, 100, 100, null);
                    Image qrImage = qrCode.GetImage();

                    qrImage.ScaleToFit(85f, 85f);
                    qrImage.Alignment = Element.ALIGN_CENTER;
                    qrImage.SpacingAfter = 0f;   // 🔴 IMPORTANT
                    document.Add(qrImage);

                    // Bold text with tight spacing
                    var font = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10);

                    var paragraph = new Paragraph(barcodeValue, font)
                    {
                        Alignment = Element.ALIGN_CENTER,
                        SpacingBefore = -14f,      // 🔴 pulls text up
                        SpacingAfter = 0f,
                        Leading = 10f             // 🔴 exact line height
                    };

                    document.Add(paragraph);

                    document.Close();

                }
                else
                {
                    // ✅ Generate CODE128 Barcode
                    var barcode128 = new Barcode128
                    {
                        CodeType = Barcode.CODE128,
                        Code = barcodeValue,
                        Font = null,
                        BarHeight = 40f,
                        X = 1.0f
                    };

                    Image barcodeImage = barcode128.CreateImageWithBarcode(writer.DirectContent, null, null);
                    barcodeImage.Alignment = Element.ALIGN_CENTER;

                    if (barcodeImage.Width > pageSize.Width - 20f)
                    {
                        float scalePercent = (pageSize.Width - 20f) / barcodeImage.Width * 100;
                        barcodeImage.ScalePercent(scalePercent);
                    }

                    document.Add(barcodeImage);

                    // ✅ 3. Add barcode text below
                    var font = FontFactory.GetFont(FontFactory.HELVETICA, 10);
                    var paragraph = new Paragraph(barcodeValue, font)
                    {
                        Alignment = Element.ALIGN_CENTER,
                        SpacingBefore = 2f
                    };
                    document.Add(paragraph);

                    document.Close();
                }


                

                // new code for printing
                string userName = User.Identity.Name;
                string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;



                var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);

                if (getUserData == null || string.IsNullOrWhiteSpace(getUserData.LabelPrinter))
                {
                    // ❌ No printer set → return PDF to browser
                    return File(ms.ToArray(), "application/pdf", "barcode.pdf");
                }
                else
                {
                    string printerName = getUserData.LabelPrinter.Trim();
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff");
                    string fileName = $"[{printerName}]_NeuMo_{timestamp}.pdf";

                    string targetFolder = @"\\MS-FXXL-55-W19\PDFToPrint\";
                    string fullPath = Path.Combine(targetFolder, fileName);

                    try
                    {
                        // 1. Save PDF
                        System.IO.File.WriteAllBytes(fullPath, ms.ToArray());

                        // 2. Insert into DB
                        Insert(fullPath, printerName);

                        // ✅ Success
                        return Json(new { success = true });
                    }
                    catch (Exception ex)
                    {
                        // ❌ Failure → return error JSON
                        return Json(new { success = false, message = "Daten konnten nicht gespeichert werden.", detail = ex.Message });
                    }
                }

            }
        }



        [HttpPost]
        public ActionResult Melden(string Comment, string bikeId, string barcode, IEnumerable<HttpPostedFileBase> Images, string UnPackingWithFrameNumber)
        {
            try
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



                if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(userCompany))
                    return Json(new { success = false, message = "Benutzerdaten fehlen." });

                bool isProduction = Convert.ToBoolean(ConfigurationManager.AppSettings["IsProduction"]);

                var getBikeData = db.vw_NeumoProductDetails.FirstOrDefault(u => u.Bar_Code == barcode && u.Company == userCompany);

                if (getBikeData == null)
                    return Json(new { success = false, message = "Produkt nicht gefunden." });

                var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
                if (getUserData == null)
                    return Json(new { success = false, message = "Benutzer nicht gefunden." });

                string location = getUserData.BranchName?.ToUpper();
                var GetRecieverData = db.tbl_Branches.FirstOrDefault(u => u.BranchName.ToUpper() == location);

                string subject = "Neuer Mangel gemeldet";
                string body = $@"
            <p style='font-family:Segoe UI, sans-serif; font-size:16px;'>
                Liebe Rekla-Abteilung,<br><br>
                bei diesem Rad wurde ein Mangel festgestellt. Bitte um Prüfung und Entscheidung für das weitere Vorgehen.<br><br>

                Artikelnummer: {getBikeData.Bar_Code} <br>
                Seriennummer: {bikeId}<br>
                Produkt-Information: {getBikeData.Brand}; {getBikeData.Description}<br><br>

                Monteur Name: {getUserData.UserName}<br>
                Kommentar vom Monteur: {System.Net.WebUtility.HtmlEncode(Comment)}<br><br>

                mit freundlichen Grüßen!
            </p>";

                using (MailMessage mail = new MailMessage())
                {
                    mail.From = new MailAddress("scanner@fahrrad-xxl.de");

                    if (!isProduction)
                    {
                        mail.To.Add("odoo@fahrrad-xxl.de");
                    }
                    else
                    {
                        string recipients = GetRecieverData?.Email ?? "odoo@fahrrad-xxl.de";

                        // Split by comma or semicolon and trim each
                        var addresses = recipients.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var addr in addresses)
                        {
                            mail.To.Add(addr.Trim());
                        }
                    }

                    mail.Bcc.Add("odoo@fahrrad-xxl.de");

                    mail.Subject = subject;
                    mail.Body = body;
                    mail.IsBodyHtml = true;

                    if (Images != null)
                    {
                        foreach (var file in Images.Where(f => f != null && f.ContentLength > 0))
                        {
                            string fileName = Path.GetFileName(file.FileName);
                            mail.Attachments.Add(new Attachment(file.InputStream, fileName));
                        }
                    }

                    using (SmtpClient client = new SmtpClient("192.168.94.254", 25))
                    {
                        client.Credentials = new NetworkCredential("scanner@fahrrad-xxl.de", "jkJyOpPAoN16ohIU");
                        client.EnableSsl = false;
                        client.Send(mail);
                    }
                }

                if (UnPackingWithFrameNumber == "1")
                {
                    var updateBikeData = db.tbl_BikePlacements
                    .FirstOrDefault(u => u.BikeID == bikeId && u.CompanyName == userCompany && u.AssemblerID == getUserData.UserId && u.Status == "UnPackingWithFrameNumber" && u.Location == location);
                    if (updateBikeData != null)
                    {
                        updateBikeData.AssembleEndTime = DateTime.Now;
                        updateBikeData.Status = "unmontiert";

                    }
                    db.SaveChanges();
                }
                else
                {
                    var updateBikeData = db.tbl_BikePlacements
                    .FirstOrDefault(u => u.BikeID == bikeId && u.CompanyName == userCompany && u.AssemblerID == getUserData.UserId && u.Status == "Assigned" && u.Location == location);
                    if (updateBikeData != null)
                    {
                        updateBikeData.AssembleEndTime = DateTime.Now;
                        updateBikeData.Status = "unmontiert";

                    }
                    else
                    {
                        var direktMontageBikeData = db.tbl_BikePlacements
                        .FirstOrDefault(u => u.BikeID == bikeId && u.CompanyName == userCompany && u.AssemblerID == getUserData.UserId && u.Status == "DirektMontage" && u.Location == getUserData.BranchName);

                        if (direktMontageBikeData != null)
                        {
                            direktMontageBikeData.AssembleEndTime = DateTime.Now;
                            direktMontageBikeData.Status = "unmontiert";

                        }

                    }
                    db.SaveChanges();
                }


                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                // Optionally log the exception (e.g., to a log file or database)
                return Json(new { success = false, message = "Fehler beim Melden: " + ex.Message });
            }
        }


        public class SerializedProduct
        {
            public string SerialNum { get; set; }
            public string ItemNo { get; set; }
            public string FrameNum { get; set; }
            public string BatteryNum { get; set; }
            public string AccessoryBag { get; set; }
            public string ForkNum { get; set; }
            public DateTime CreatedAt { get; set; }
        }
        public class BikeReportViewModel
        {
            public string UserName { get; set; }
            public int TotalBikes { get; set; }
            public int DifficultBikes { get; set; }
            public int EasyBikes { get; set; }
            public int DefectedBikes { get; set; }
        }
        public class BranchViewModel
        {
            public int ID { get; set; }
            public string BranchName { get; set; }
            public bool? Active { get; set; }
            public string Email { get; set; }
            public string CompanyName { get; set; }
            public string FlowType { get; set; }
        }
        public class RpcResponse
        {
            [JsonProperty("jsonrpc")]
            public string Jsonrpc { get; set; }

            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("result")]
            public JToken Result { get; set; }

            [JsonProperty("error")]
            public JToken Error { get; set; }
        }

        [Authorize]
        public ActionResult FahrradPassRePrint()
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

            return View();
        }
        [Authorize]
        public ActionResult Reporting(DateTime? startDate, DateTime? endDate, string branchName = "")
        {
           
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

            var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
            var branchDataForEmployee = db.tbl_Branches.FirstOrDefault(u => u.BranchName == getUserData.BranchName);

            ViewBag.Flow = branchDataForEmployee.FlowType;
            ViewBag.IsAdmin = getUserData.IsAdmin;
            ViewBag.IsUnpacker = getUserData.IsUnpacker;
            ViewBag.IsMechanic = getUserData.IsMechanic;
            ViewBag.BranchName = getUserData.BranchName;

            ViewBag.Branches = db.tbl_Branches
                     .Select(b => b.BranchName)
                     .Distinct()
                     .ToList();

            // ✅ Set date range defaults
            if (!startDate.HasValue || !endDate.HasValue)
            {
                startDate = DateTime.Today;
                endDate = DateTime.Today.AddDays(1).AddSeconds(-1);
            }
            else
            {
                endDate = endDate.Value.Date.AddDays(1).AddSeconds(-1);
            }

            // ✅ Base queries for assembled and defected bikes
            var assembledBikes = db.tbl_BikePlacements
                .Where(b => b.Status == "montiert" && b.AssembleEndTime >= startDate && b.AssembleEndTime <= endDate);

            var defectedBikes = db.tbl_BikePlacements
                .Where(b => b.Status == "unmontiert" && b.AssembleEndTime >= startDate && b.AssembleEndTime <= endDate);

            // ✅ Filter by branch if selected
            if (!string.IsNullOrEmpty(branchName))
            {
                var userIdsInBranch = db.Users
                    .Where(u => u.BranchName == branchName && u.CompanyName == userCompany)
                    .Select(u => u.UserId)
                    .ToList(); // this is a List<int>

                // ✅ Compare using .Value and check HasValue to avoid null errors
                assembledBikes = assembledBikes
                    .Where(b => b.AssemblerID.HasValue && userIdsInBranch.Contains(b.AssemblerID.Value));

                defectedBikes = defectedBikes
                    .Where(b => b.AssemblerID.HasValue && userIdsInBranch.Contains(b.AssemblerID.Value));
            }

            var assembledList = assembledBikes.ToList();
            var defectedList = defectedBikes.ToList();

            ViewBag.DefectedBikes = defectedList.Count;
            ViewBag.StartDate = startDate.Value.ToString("yyyy-MM-dd");
            ViewBag.EndDate = endDate.Value.ToString("yyyy-MM-dd");
            ViewBag.SelectedBranch = branchName;

            var userIds = assembledList.Select(b => b.Mechaniker).Distinct().ToList();

            var users = db.Users
                .Where(u => userIds.Contains(u.UserName) && u.CompanyName == userCompany)
                .ToList();

            

            var reportData = users.Select(user =>
            {
                var userBikes = assembledList.Where(b => b.Mechaniker == user.UserName).ToList();
                return new BikeReportViewModel
                {
                    UserName = user.UserName,
                    TotalBikes = userBikes.Count,
                    DifficultBikes = userBikes.Count(b => b.IsDifficult == true),
                    EasyBikes = userBikes.Count(b => b.IsDifficult == false)
                };
            }).ToList();


            return View(reportData);
        }

        [HttpGet]
        public ActionResult ExportUserReport(DateTime startDate, DateTime endDate, string branchName = "")
        {
            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            // ✅ Include the whole day for the end date
            DateTime nextDay = endDate.AddDays(1);

            // ✅ Base query
            var assembledBikes = db.tbl_BikePlacements
                .Where(b => b.Status == "montiert" &&
                            b.AssembleEndTime >= startDate &&
                            b.AssembleEndTime < nextDay);

            // ✅ If branch selected, filter by its users
            if (!string.IsNullOrEmpty(branchName))
            {
                var userIdsInBranch = db.Users
                    .Where(u => u.BranchName == branchName && u.CompanyName == userCompany)
                    .Select(u => u.UserName)
                    .ToList();

                assembledBikes = assembledBikes
                    .Where(b => b.Mechaniker != null && userIdsInBranch.Contains(b.Mechaniker));
            }

            var assembledList = assembledBikes.ToList();

            if (!assembledList.Any())
            {
                return Json(new
                {
                    success = false,
                    message = "Keine Daten im ausgewählten Zeitraum gefunden."
                }, JsonRequestBehavior.AllowGet);
            }

            var userIds = assembledList.Select(b => b.Mechaniker).Distinct().ToList();

            var users = db.Users
                .Where(u => userIds.Contains(u.UserName) && u.CompanyName == userCompany)
                .ToList();

            // --- Create DataTable for Excel ---
            DataTable dt = new DataTable("UserReport");
            dt.Columns.Add("Datum", typeof(string));
            dt.Columns.Add("Mitarbeiter", typeof(string));
            dt.Columns.Add("Mitarbeiternummer", typeof(string));
            dt.Columns.Add("Anzahl montierte Räder", typeof(string));
            dt.Columns.Add("Anzahl E-Bikes", typeof(string));
            dt.Columns.Add("Anzahl Bio-Bikes", typeof(string));
            dt.Columns.Add("Anzahl Schwere Räder", typeof(string));
            dt.Columns.Add("Anzahl Leichte Räder", typeof(string));

            // --- Fill DataTable ---
            foreach (var user in users)
            {
                var userBikes = assembledList.Where(b => b.Mechaniker == user.UserName).ToList();
                if (!userBikes.Any()) continue;

                int totalBikes = userBikes.Count;
                int eBikes = userBikes.Count(b => b.IsEbike == true);
                int bioBikes = userBikes.Count(b => b.IsEbike == false);
                int difficult = userBikes.Count(b => b.IsDifficult == true);
                int easy = userBikes.Count(b => b.IsDifficult == false || b.IsDifficult == null);

                dt.Rows.Add(
                    DateTime.Now.ToString("yyyy-MM-dd"),
                    user.UserName,
                    user.EmployeeNumber,
                    totalBikes,
                    eBikes,
                    bioBikes,
                    difficult,
                    easy
                );
            }

            // --- Create Excel workbook using ClosedXML ---
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add(dt, "User Report");

                // Optional styling for readability
                var headerRange = worksheet.Range("A1:H1");
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                worksheet.Columns().AdjustToContents();

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    var fileName = $"User_Report_{branchName}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}.xlsx";

                    return File(
                        content,
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        fileName
                    );
                }
            }
        }


        [Authorize]
        public ActionResult BikePassLog(string search, string branch, int page = 1)
        
        {
            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(userCompany))
                return RedirectToAction("Login", "Account");

            string initials = userName.Contains(" ")
                ? string.Concat(userName.Split(' ')[0][0], userName.Split(' ')[1][0]).ToUpper()
                : userName[0].ToString().ToUpper();

            ViewBag.UserName = initials;

            var getUserData = db.Users.FirstOrDefault(u => u.UserName == userName && u.CompanyName == userCompany);
            var branchDataForEmployee = db.tbl_Branches.FirstOrDefault(u => u.BranchName == getUserData.BranchName);

            ViewBag.Flow = branchDataForEmployee.FlowType;
            ViewBag.IsAdmin = getUserData.IsAdmin;
            ViewBag.IsUnpacker = getUserData.IsUnpacker;
            ViewBag.IsMechanic = getUserData.IsMechanic;
            ViewBag.CurrentFilter = search;
            ViewBag.CurrentBranch = branch;
            ViewBag.BranchName = getUserData.BranchName;

            //var getBikepassData = db.tbl_BikePassPrint_Log
            //.Where(u => u.Mechaniker != "Migartion" && u.Mechaniker != null);
            var query = db.tbl_BikePassPrint_Log.AsQueryable();



            // 🔍 Search filter
            if (!string.IsNullOrEmpty(search))
            {
                //getBikepassData = getBikepassData.Where(u =>
                //    u.SerialNumber.Contains(search) ||
                //    u.Frame_Num.Contains(search));
                query = query.Where(u =>
                u.Barcode.Contains(search) || u.SerialNumber.Contains(search) || u.AccesoryBag.Contains(search) || u.BatteryNumber.Contains(search) || u.KeyNumber.Contains(search) ||
                 u.Frame_Num.Contains(search));
            }
            // 🏢 Branch filter
            if (!string.IsNullOrEmpty(branch))
            {
                var LogixBranch = "";

                if(branch == "LEI")
                {
                    LogixBranch = "1";
                }
                else if(branch == "DDN")
                {
                    LogixBranch = "2";
                }
                else if (branch == "CHE")
                {
                    LogixBranch = "3";
                }
                else if (branch == "HAL")
                {
                    LogixBranch = "6";
                }
                else if (branch == "KES")
                {
                    LogixBranch = "8";
                }
                query = query.Where(u => u.BranchName == branch || u.BranchName == LogixBranch);
            }

            //var orderedData = getBikepassData.OrderByDescending(u => u.Date);
            query = query.OrderByDescending(u => u.Date);

            // 📄 Pagination (10 items per page)
            int pageSize = 500;
            var pagedList = query.ToPagedList(page, pageSize);

            ViewBag.Branches = db.tbl_Branches
                              .Select(b => b.BranchName)
                              .Distinct()
                              .OrderBy(b => b)
                             .ToList();

            return View(pagedList);
        }
        

        [Authorize]
        public ActionResult BranchesData()
        {
            //string userName = Session["UserName"].ToString();
            //string userCompany = Session["CompanyName"].ToString();

            //string userName = "sajid";
            //string userCompany = "Emporon";

            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            var companies = db.tbl_Company.ToList();
            ViewBag.Companies = new SelectList(companies, "ID", "Name");

            ViewBag.Branches = new SelectList(db.tbl_Branches.ToList(), "ID", "BranchName");

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

            ViewBag.UserNameAndCompany = userName + userCompany;


            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();
            var branchDataForEmployee = db.tbl_Branches.FirstOrDefault(u => u.BranchName == getUserData.BranchName);

            ViewBag.Flow = branchDataForEmployee.FlowType;
            ViewBag.IsAdmin = getUserData.IsAdmin;
            ViewBag.IsUnpacker = getUserData.IsUnpacker;
            ViewBag.IsMechanic = getUserData.IsMechanic;
            ViewBag.BranchName = getUserData.BranchName;

            var getBranches = (from b in db.tbl_Branches
                               join c in db.tbl_Company on b.CompanyId equals c.ID
                               select new BranchViewModel
                               {
                                   ID = b.ID,
                                   BranchName = b.BranchName,
                                   Active = b.Active,
                                   Email = b.Email,
                                   CompanyName = c.Name,
                                   FlowType = b.FlowType
                               }).ToList();

            return View(getBranches);
        }
        public JsonResult GetBranch(int id)
        {
            var branch = db.tbl_Branches.FirstOrDefault(b => b.ID == id);
            return Json(branch, JsonRequestBehavior.AllowGet); // Allow GET
        }

        [HttpPost]
        public JsonResult UpdateFlowType(int id, string flowType, string email)
        {
            var branch = db.tbl_Branches.FirstOrDefault(b => b.ID == id);
            if (branch == null)
                return Json(new { success = false, message = "Branch not found" });

            branch.FlowType = flowType;
            branch.Email = email;
            db.SaveChanges();

            return Json(new { success = true });
        }

        [Authorize]
        public ActionResult DirektMontage()
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

            ViewBag.UserNameAndCompany = userName + userCompany;


            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();
            ViewBag.IsAdmin = getUserData.IsAdmin;
            ViewBag.IsUnpacker = getUserData.IsUnpacker;
            ViewBag.IsMechanic = getUserData.IsMechanic;
            ViewBag.BranchName = getUserData.BranchName;



            return View();
        }
        [HttpPost]
        public JsonResult getDataForDirektMontage(string SerialNo)
        
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



            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();
            if (getUserData == null)
            {
                return Json(new { success = false, message = "Benutzerdaten existieren nicht." });
            }

            string imageUrl = null;

            var mechanicList = db.Users.Where(U => U.BranchName == getUserData.BranchName)
              .Select(u => new { u.UserId, u.UserName }) // include only necessary fields
              .ToList();

            var getBikePassData = db.tbl_BikePassPrint_Log.Where(u => u.SerialNumber == SerialNo && u.CompanyName == userCompany && u.AccesoryBag == "--" && u.BatteryNumber == "--" && u.KeyNumber == "--").FirstOrDefault();
            if (getBikePassData != null)
            {
                var getBikeDataWithBarcode = db.vw_NeumoProductDetails.Where(u => u.Company == userCompany && u.Bar_Code == getBikePassData.Barcode).FirstOrDefault();

                var updateBikeData = db.tbl_BikePlacements.Where(u => u.BikeID == getBikePassData.SerialNumber && u.CompanyName == userCompany && u.BikeID == SerialNo && u.Status == "UnPackingWithFrameNumber").FirstOrDefault();
                if (updateBikeData == null)
                {
                    return Json(new { success = false, message = "Fahrrad nicht gefunden oder bereits montiert." });
                }

                updateBikeData.AssembleStartTime = DateTime.Now;
                updateBikeData.Location = getUserData.BranchName;
                updateBikeData.AssemblerID = getUserData.UserId;
                db.SaveChanges();

                bool IsRCategory = !string.IsNullOrEmpty(getBikeDataWithBarcode?.No_) &&
                   getBikeDataWithBarcode.No_[0].ToString().Equals("R", StringComparison.OrdinalIgnoreCase);

                return Json(new
                {
                    success = true,
                    data = getBikeDataWithBarcode,
                    imageUrl = "",
                    unpackingWithFrameNumber = 1,
                    rahmenNumber = updateBikeData.FrameNumber,
                    SerialNo = updateBikeData.BikeID,
                    mechanics = mechanicList,
                    isRCategory = IsRCategory ,
                    currentUserName = getUserData.UserName,
                });

            }

            var isAlreadyScanned = db.tbl_BikePlacements.Where(u => u.BikeID == SerialNo.Trim() && u.Status != "unmontiert" && u.Status != "abbrechen" && u.Status != "DirektMontage").FirstOrDefault();
            if (isAlreadyScanned != null)
            {
                return Json(new { success = false, message = "Dieses Fahrrad wurde bereits gescannt. Bitte prüfe das Fahrradpaß-Log." });
            }

            var odooService = new OdooService();
            var getBarcodeId = odooService.GetStockLotId(SerialNo);

            if (string.IsNullOrEmpty(getBarcodeId))
            {
                return Json(new { success = false, message = "Fahrraddaten sind in der API nicht vorhanden." });
            }

            var getBikeData = db.vw_NeumoProductDetails.Where(u => u.Company == userCompany && u.Bar_Code == getBarcodeId).FirstOrDefault();
            if (getBikeData != null)
            {
                db.tbl_BikePlacements.Add(new tbl_BikePlacements
                {
                    BoxName = "",
                    Status = "DirektMontage",
                    IsOccupied = false,
                    IsPriority = false,
                    Location = getUserData.BranchName,
                    DateTime = DateTime.Now,
                    BikeID = SerialNo.Trim(),
                    AssemblerID = getUserData.UserId,
                    AssembleStartTime = DateTime.Now,
                    IsEbike = getBikeData.isebike,
                    CompanyName = userCompany,
                    Barcode = getBarcodeId
                });

                db.SaveChanges();
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

            bool isRCategory = !string.IsNullOrEmpty(getBikeData?.No_) &&
                   getBikeData.No_[0].ToString().Equals("R", StringComparison.OrdinalIgnoreCase);


            return Json(new
            {
                success = true,
                data = getBikeData,
                imageUrl = imageUrl,
                unpackingWithFrameNumber = 0,
                rahmenNumber = "",
                SerialNo = SerialNo,
                mechanics = mechanicList,
                isRCategory = isRCategory,
                currentUserName = getUserData.UserName,
            });
        }
        [Authorize]
        public ActionResult FlexMontage()
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

            ViewBag.UserNameAndCompany = userName + userCompany;


            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();
            var branchDataForEmployee = db.tbl_Branches.FirstOrDefault(u => u.BranchName == getUserData.BranchName);

            ViewBag.Flow = branchDataForEmployee.FlowType;

            ViewBag.IsAdmin = getUserData.IsAdmin;
            ViewBag.IsUnpacker = getUserData.IsUnpacker;
            ViewBag.IsMechanic = getUserData.IsMechanic;
            ViewBag.BranchName = getUserData.BranchName;

            // Step 1: Get latest placements (in memory)
            var latestPlacements = db.tbl_BikePlacements
                .Where(p => p.IsOccupied && p.Location == getUserData.BranchName && p.Status != "Assigned")
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
        [Authorize]
        public ActionResult Rahmennummer()
        {
            

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

            ViewBag.UserNameAndCompany = userName + userCompany;


            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();
            var branchDataForEmployee = db.tbl_Branches.FirstOrDefault(u => u.BranchName == getUserData.BranchName);

            ViewBag.Flow = branchDataForEmployee.FlowType;

            ViewBag.IsAdmin = getUserData.IsAdmin;
            ViewBag.IsUnpacker = getUserData.IsUnpacker;
            ViewBag.IsMechanic = getUserData.IsMechanic;
            ViewBag.BranchName = getUserData.BranchName;

            var getAllFrameList = db.tbl_FrameNumberPattern
                                  .GroupBy(x => x.BrandDesignation)
                                  .Select(g => g.FirstOrDefault())       // one entry per brand
                                    .OrderBy(x => x.BrandDesignation)      // sort alphabetically
                                 .ToList();

            return View(getAllFrameList);
        }
        public JsonResult GetPatternsByBrand(string brand)
        {
            var patterns = db.tbl_FrameNumberPattern
                .Where(x => x.BrandDesignation == brand)
                .Select(x => new {
                    x.ID,
                    x.Pattern
                })
                .ToList();

            return Json(patterns, JsonRequestBehavior.AllowGet);
        }
        [HttpPost]
        public JsonResult AddPattern(string pattern, string brand)
        {
            brand = brand.ToUpper();
            try
            {
                var save = new tbl_FrameNumberPattern
                {
                    Pattern = pattern,
                    BrandDesignation = brand
                };

                db.tbl_FrameNumberPattern.Add(save);
                db.SaveChanges();

                return Json(new { success = true, id = save.ID });
            }
            catch
            {
                return Json(new { success = false });
            }
        }

        [HttpPost]
        public JsonResult DeletePattern(int id)
        {
            try
            {
                // Fetch the pattern from database by ID
                var pattern = db.tbl_FrameNumberPattern.FirstOrDefault(p => p.ID == id);
                if (pattern == null)
                {
                    return Json(new { success = false, message = "Muster nicht gefunden." });
                }

                // Remove it
                db.tbl_FrameNumberPattern.Remove(pattern);
                db.SaveChanges();

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}