"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { api, User, auth } from "@/lib/api";

export default function ProfilePage() {
  const router = useRouter();
  const [user, setUser] = useState<User | null>(null);
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  const [darkMode, setDarkMode] = useState(false);

  useEffect(() => {
    if (!auth.isAuthenticated()) {
      router.push("/login");
      return;
    }
    loadUser();
    // Load dark mode preference
    const savedMode = localStorage.getItem("darkMode");
    if (savedMode === "true") {
      setDarkMode(true);
      document.documentElement.classList.add("dark");
    }
  }, [router]);

  useEffect(() => {
    if (darkMode) {
      document.documentElement.classList.add("dark");
      localStorage.setItem("darkMode", "true");
    } else {
      document.documentElement.classList.remove("dark");
      localStorage.setItem("darkMode", "false");
    }
  }, [darkMode]);

  const loadUser = async () => {
    try {
      const response = await api.getAccount();
      if (response.success) {
        setUser(response.data);
        setFirstName(response.data.firstName);
        setLastName(response.data.lastName);
      }
    } catch (err) {
      setError("Failed to load profile");
      console.error(err);
    } finally {
      setLoading(false);
    }
  };

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError("");
    setSuccess("");

    try {
      const response = await api.updateAccount({ firstName, lastName });
      if (response.success) {
        setUser(response.data);
        setFirstName(response.data.firstName);
        setLastName(response.data.lastName);
        auth.saveUser(response.data);
        setSuccess("Profile updated successfully");
      }
    } catch (err) {
      setError("Failed to update profile");
      console.error(err);
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 dark:bg-gray-900 flex items-center justify-center transition-colors">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-black dark:border-white"></div>
      </div>
    );
  }

  const initials = user
    ? `${user.firstName.charAt(0)}${user.lastName.charAt(0)}`.toUpperCase()
    : "";

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900 flex flex-col items-center justify-center gap-6 p-10 transition-colors relative">
      {/* Back Button - Top Left */}
      <button
        onClick={() => router.back()}
        className="absolute top-6 left-6 flex items-center gap-2 text-sm text-gray-600 dark:text-gray-400 hover:text-black dark:hover:text-white transition-colors"
      >
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
        </svg>
        Back
      </button>

      {/* Header */}
      <div className="w-full max-w-lg bg-white dark:bg-gray-800 rounded-2xl p-5 transition-colors">
        <h1 className="text-xl font-bold text-gray-900 dark:text-white">Profile</h1>
        <p className="text-gray-500 dark:text-gray-400 text-sm mt-1">
          Manage your account settings
        </p>
      </div>

      {error && (
        <div className="bg-red-50 dark:bg-red-900/20 text-red-600 dark:text-red-400 px-4 py-2 rounded-lg text-sm max-w-lg w-full">
          {error}
        </div>
      )}

      {success && (
        <div className="bg-green-50 dark:bg-green-900/20 text-green-600 dark:text-green-400 px-4 py-2 rounded-lg text-sm max-w-lg w-full">
          {success}
        </div>
      )}

      {/* Profile Form */}
      <form
        onSubmit={handleSave}
        className="w-full max-w-lg bg-white dark:bg-gray-800 rounded-xl p-6 space-y-6 transition-colors"
      >
        {/* Avatar and Info */}
        <div className="flex items-center gap-4">
          <div className="w-16 h-16 bg-gradient-to-br from-gray-800 to-gray-900 dark:from-gray-600 dark:to-gray-700 rounded-full flex items-center justify-center">
            <span className="text-white text-sm font-semibold">{initials}</span>
          </div>
          <div>
            <p className="font-semibold text-gray-900 dark:text-white">
              {user?.firstName} {user?.lastName}
            </p>
            <p className="text-gray-500 dark:text-gray-400 text-sm">{user?.email}</p>
          </div>
        </div>

        <hr className="border-gray-100 dark:border-gray-700" />

        {/* Name Fields */}
        <div className="space-y-4">
          <div className="flex gap-4">
            <div className="flex-1">
              <label className="block text-sm font-medium mb-1.5 text-gray-900 dark:text-white">
                First Name
              </label>
              <input
                type="text"
                value={firstName}
                onChange={(e) => setFirstName(e.target.value)}
                className="w-full px-3 py-2.5 bg-gray-100 dark:bg-gray-700 rounded-lg text-sm outline-none focus:ring-2 focus:ring-black/10 dark:focus:ring-white/10 text-gray-900 dark:text-white"
              />
            </div>
            <div className="flex-1">
              <label className="block text-sm font-medium mb-1.5 text-gray-900 dark:text-white">
                Last Name
              </label>
              <input
                type="text"
                value={lastName}
                onChange={(e) => setLastName(e.target.value)}
                className="w-full px-3 py-2.5 bg-gray-100 dark:bg-gray-700 rounded-lg text-sm outline-none focus:ring-2 focus:ring-black/10 dark:focus:ring-white/10 text-gray-900 dark:text-white"
              />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium mb-1.5 text-gray-900 dark:text-white">Email</label>
            <input
              type="email"
              value={user?.email || ""}
              disabled
              className="w-full px-3 py-2.5 bg-gray-100 dark:bg-gray-700 rounded-lg text-sm text-gray-500 dark:text-gray-400 cursor-not-allowed"
            />
          </div>
        </div>

        <button
          type="submit"
          disabled={saving}
          className="px-4 py-2 bg-gray-900 dark:bg-white text-white dark:text-gray-900 text-sm font-medium rounded-lg hover:bg-gray-800 dark:hover:bg-gray-100 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {saving ? "Saving..." : "Save Changes"}
        </button>
      </form>

      {/* Preferences */}
      <div className="w-full max-w-lg bg-white dark:bg-gray-800 rounded-xl p-6 space-y-4 transition-colors">
        <h2 className="font-semibold text-gray-900 dark:text-white">Preferences</h2>

        <div className="flex justify-between items-center">
          <div>
            <p className="font-medium text-sm text-gray-900 dark:text-white">Dark Mode</p>
            <p className="text-gray-500 dark:text-gray-400 text-xs">
              Switch between light and dark theme
            </p>
          </div>
          <button
            onClick={() => setDarkMode(!darkMode)}
            className={`w-12 h-7 rounded-full p-0.5 transition-colors ${
              darkMode ? "bg-gray-900 dark:bg-white" : "bg-gray-200 dark:bg-gray-600"
            }`}
          >
            <div
              className={`w-6 h-6 bg-white dark:bg-gray-900 rounded-full shadow-sm transition-transform ${
                darkMode ? "translate-x-5" : "translate-x-0"
              }`}
            />
          </button>
        </div>

      </div>
    </div>
  );
}
