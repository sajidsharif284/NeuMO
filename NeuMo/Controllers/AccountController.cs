using NeuMo.NeuMoDatabase;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Security.Cryptography;
using System.Web;
using System.Web.Helpers;
using System.Web.Mvc;
using System.Web.Security;

namespace NeuMo.Controllers
{
    public class AccountController : Controller
    {
        NeuMoEntities db = new NeuMoEntities();
        // GET: Account
        public ActionResult Login()
        {
            return View();
        }
        [HttpPost]
        public ActionResult Login(string adminUsername, string employeeUsername, string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                var emplyoyeeData = db.Users.FirstOrDefault(u => u.EmployeeNumber == employeeUsername);
                if (emplyoyeeData.IsAdmin == true)
                {
                    ViewBag.LoginType = "admin";
                    ViewBag.AdminError = "Sie sind Administrator. Bitte geben Sie diese Anmeldeinformationen ein.";
                    return View();
                }

                if (emplyoyeeData == null)
                {
                    ViewBag.LoginType = "employee";
                    ViewBag.EmployeeError = "Mitarbeiternummer ist falsch.";
                    return View();
                }
                else
                {
                    if (!emplyoyeeData.IsActive)
                    {
                        ViewBag.LoginType = "employee";
                        ViewBag.EmployeeError = "Dieser Benutzer ist deaktiviert. Bitte wenden Sie sich an den Administrator.";
                        return View();
                    }
                    var tickets = new FormsAuthenticationTicket(
                        1,
                        emplyoyeeData.UserName,
                        DateTime.Now,
                        DateTime.Now.AddMinutes(480),
                        true,
                        emplyoyeeData.CompanyName
                        );

                    string encTickets = FormsAuthentication.Encrypt(tickets);
                    var cookies = new HttpCookie(FormsAuthentication.FormsCookieName, encTickets);
                    Response.Cookies.Add(cookies);
                    // 🔐 Set Forms Auth cookie
                    //FormsAuthentication.SetAuthCookie(emplyoyeeData.UserName, true);

                    // Optionally set session values
                    Session["UserName"] = emplyoyeeData.UserName;
                    Session["CompanyName"] = emplyoyeeData.CompanyName;

                    var branchDataForEmployee = db.tbl_Branches.FirstOrDefault(u => u.BranchName == emplyoyeeData.BranchName);
                    if (branchDataForEmployee != null)
                    {
                        if (branchDataForEmployee.FlowType == "Standard, Feste Auspacker & Montage")
                        {
                            if (emplyoyeeData.IsUnpacker == true || emplyoyeeData.IsAdmin == true)
                            {
                                return RedirectToAction("UnPackingAssign", "UnPacking");
                            }
                            else if (emplyoyeeData.IsMechanic == true || emplyoyeeData.IsAdmin == true)
                            {
                                return RedirectToAction("FahrradMontage", "Assembly");
                            }
                            else
                            {
                                return RedirectToAction("Index", "Home");
                            }
                        }
                        else if (branchDataForEmployee.FlowType == "Direktmontage, Nur Monteure")
                        {
                            return RedirectToAction("DirektMontage", "Assembly");
                        }
                        else if (branchDataForEmployee.FlowType == "FlexModell, Flexible Auspacker & Monteur")
                        {
                            return RedirectToAction("FlexMontage", "Assembly");
                        }

                    }

                    return RedirectToAction("UnPackingAssign", "UnPacking");
                }

            }
            var user = db.Users.FirstOrDefault(u => u.UserName == adminUsername);

            if (user == null || !VerifyPassword(password, user.Password))
            {
                ViewBag.LoginType = "admin";
                ViewBag.AdminError = "Benutzername oder Passwort ist falsch.";
                return View();
            }
            if (!user.IsActive)
            {
                ViewBag.LoginType = "admin";
                ViewBag.AdminError = "Dieser Benutzer ist deaktiviert. Bitte wenden Sie sich an den Administrator.";
                return View();
            }
            var ticket = new FormsAuthenticationTicket(
                         1,
                         user.UserName,
                         DateTime.Now,
                         DateTime.Now.AddMinutes(480),
                         true,
                          user.CompanyName
                         );

            string encTicket = FormsAuthentication.Encrypt(ticket);
            var cookie = new HttpCookie(FormsAuthentication.FormsCookieName, encTicket);
            Response.Cookies.Add(cookie);


            // 🔐 Set Forms Auth cookie
            //FormsAuthentication.SetAuthCookie(user.UserName, true);

            // Optionally set session values
            Session["UserName"] = user.UserName;
            Session["CompanyName"] = user.CompanyName;

            var branchData = db.tbl_Branches.FirstOrDefault(u => u.BranchName == user.BranchName);
            if (branchData != null)
            {
                if (branchData.FlowType == "Standard, Feste Auspacker & Montage")
                {
                    if (user.IsUnpacker == true || user.IsAdmin == true)
                    {
                        return RedirectToAction("UnPackingAssign", "UnPacking");
                    }
                    else if (user.IsMechanic == true || user.IsAdmin == true)
                    {
                        return RedirectToAction("FahrradMontage", "Assembly");
                    }
                    else
                    {
                        return RedirectToAction("Index", "Home");
                    }
                }
                else if (branchData.FlowType == "Direktmontage, Nur Monteure")
                {
                    return RedirectToAction("DirektMontage", "Assembly");
                }
                else if (branchData.FlowType == "FlexModell, Flexible Auspacker & Monteur")
                {
                    return RedirectToAction("FlexMontage", "Assembly");
                }
            }
            return RedirectToAction("Index", "Home");

        }

        [Authorize]
        public ActionResult Register()
        {
            //string userName = Session["UserName"].ToString();
            //string userCompany = Session["CompanyName"].ToString();

            string userName = User.Identity.Name;
            string userCompany = ((FormsIdentity)User.Identity).Ticket.UserData;

            ViewBag.IsAdmin = true;
            var users = db.Users.ToList();
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
            var getUserData = db.Users.Where(u => u.UserName == userName && u.CompanyName == userCompany).FirstOrDefault();
            var branchDataForEmployee = db.tbl_Branches.FirstOrDefault(u => u.BranchName == getUserData.BranchName);

            ViewBag.Flow = branchDataForEmployee.FlowType;
            ViewBag.BranchName = getUserData.BranchName;


            return View(users); // optionally pass to the view
        }
        [HttpPost]
        public ActionResult Register(Users user, string CompanyName, string BranchName, string[] SelectedRoles)
        {
            var existedUser = db.Users.FirstOrDefault(u => u.UserName == user.UserName || (u.EmployeeNumber == user.EmployeeNumber && u.EmployeeNumber != null));
            if (existedUser != null)
            {
                TempData["AlertMessage"] = "User already registered!";
            }
            else
            {
                string salt;
                string hashedPassword = HashPassword(user.Password, out salt);

                // Reset all roles
                user.IsAdmin = false;
                user.IsUnpacker = false;
                user.IsMechanic = false;

                if (SelectedRoles != null)
                {
                    if (SelectedRoles.Contains("Admin"))
                    {
                        user.IsAdmin = true;
                    }
                    else
                    {
                        if (SelectedRoles.Contains("Unpacker"))
                            user.IsUnpacker = true;

                        if (SelectedRoles.Contains("Mechanic"))
                            user.IsMechanic = true;
                    }
                }

                var newUser = new Users
                {
                    UserName = user.UserName,
                    Email = user.Email,
                    Password = $"{salt}:{hashedPassword}",
                    CreateDate = DateTime.Now,
                    IsActive = true,

                    IsAdmin = user.IsAdmin,
                    IsUnpacker = user.IsUnpacker,
                    IsMechanic = user.IsMechanic,
                    CompanyName = CompanyName,
                    BranchName = BranchName,
                    EmployeeNumber = user.EmployeeNumber,
                    BikepassPrinter = user.BikepassPrinter,
                    LabelPrinter = user.LabelPrinter
                };

                db.Users.Add(newUser);
                db.SaveChanges();
            }

            var users = db.Users.ToList();
            return RedirectToAction("Register", "Account");
        }
        [HttpGet]
        public JsonResult GetBranchesByCompany(int companyId)
        {
            var branches = db.tbl_Branches
                            .Where(b => b.CompanyId == companyId)
                            .Select(b => new { b.ID, b.BranchName })
                            .ToList();

            return Json(branches, JsonRequestBehavior.AllowGet);
        }
        [HttpGet]
        public JsonResult GetBranchesByCompanyForEdit(string companyName)
        {
            var company = db.tbl_Company.FirstOrDefault(c => c.Name == companyName);
            if (company == null)
            {
                return Json(new List<object>(), JsonRequestBehavior.AllowGet);
            }

            var branches = db.tbl_Branches
                            .Where(b => b.CompanyId == company.ID)
                            .Select(b => new { value = b.BranchName, text = b.BranchName })
                            .ToList();

            return Json(branches, JsonRequestBehavior.AllowGet);
        }

        public static string HashPassword(string password, out string salt)
        {
            // Generate a 128-bit salt
            byte[] saltBytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(saltBytes);
            }

            // Derive a 256-bit subkey (use HMACSHA256 with 10000 iterations)
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 10000))
            {
                byte[] hashBytes = pbkdf2.GetBytes(32); // 256 bits
                salt = Convert.ToBase64String(saltBytes);
                return Convert.ToBase64String(hashBytes);
            }
        }
        public bool VerifyPassword(string enteredPassword, string storedPassword)
        {
            var parts = storedPassword.Split(':');
            if (parts.Length != 2)
                return false;

            var salt = parts[0];
            var storedHash = parts[1];

            var enteredHash = HashPasswordForLogin(enteredPassword, salt);

            return enteredHash == storedHash;
        }
        public static string HashPasswordForLogin(string password, string salt)
        {
            byte[] saltBytes = Convert.FromBase64String(salt);

            using (var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 10000))
            {
                byte[] hashBytes = pbkdf2.GetBytes(32); // 256 bits
                return Convert.ToBase64String(hashBytes);
            }
        }
        [HttpPost]
        public ActionResult EditUserData(Users user, string[] SelectedRoles, string[] IsActive, string CompanyName, string BranchName)
        {
            if (!string.IsNullOrEmpty(user.EmployeeNumber))
            {
                var existedUser = db.Users
                    .FirstOrDefault(u => u.EmployeeNumber == user.EmployeeNumber && u.UserId != user.UserId);

                if (existedUser != null)
                {
                    TempData["AlertMessage"] = "Employee Number should be unique.";
                    return RedirectToAction("Register");
                }
            }


            var newPassword = user.Password;
            var existingUser = db.Users.Find(user.UserId);

            if (existingUser == null)
            {
                TempData["AlertMessage"] = "User not found.";
                return RedirectToAction("Register");
            }

            // Update fields
            existingUser.UserName = user.UserName;
            existingUser.Email = user.Email;
            existingUser.EmployeeNumber = user.EmployeeNumber;

            if (!string.IsNullOrWhiteSpace(newPassword))
            {
                string salt;
                string hashedPassword = HashPassword(newPassword, out salt);
                existingUser.Password = $"{salt}:{hashedPassword}";
            }

            existingUser.IsAdmin = false;
            existingUser.IsUnpacker = false;
            existingUser.IsMechanic = false;

            // Assign selected roles
            if (SelectedRoles != null)
            {
                if (SelectedRoles.Contains("Admin"))
                {
                    existingUser.IsAdmin = true;
                }
                else
                {
                    if (SelectedRoles.Contains("Unpacker"))
                        existingUser.IsUnpacker = true;

                    if (SelectedRoles.Contains("Mechanic"))
                        existingUser.IsMechanic = true;
                }
            }

            if (IsActive != null)
            {
                if (IsActive.Contains("Active"))
                {
                    existingUser.IsActive = true;
                }

            }
            else
            {
                existingUser.IsActive = false;
            }

            //existingUser.CompanyName = CompanyName;
            existingUser.BranchName = BranchName;
            existingUser.BikepassPrinter = user.BikepassPrinter;
            existingUser.LabelPrinter = user.LabelPrinter;

            db.SaveChanges();

            TempData["AlertMessage"] = "User updated successfully.";
            return RedirectToAction("Register");
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteUser(int id)
        {
            // Replace with actual deletion logic
            var user = db.Users.Find(id);
            if (user != null)
            {
                //db.Users.Remove(user);

                user.IsActive = false;
                db.SaveChanges();
                TempData["AlertMessage"] = "User deleted successfully.";
            }

            return RedirectToAction("Register");
        }


        [HttpPost]
        public ActionResult Logout()
        {
            FormsAuthentication.SignOut();
            Session.Clear();
            Session.Abandon();

            if (Request.Cookies[FormsAuthentication.FormsCookieName] != null)
            {
                var cookie = new HttpCookie(FormsAuthentication.FormsCookieName)
                {
                    Expires = DateTime.Now.AddDays(-1),
                    HttpOnly = true,
                    Secure = true
                };
                Response.Cookies.Add(cookie);
            }

            return RedirectToAction("Login", "Account");
        }



    }
}